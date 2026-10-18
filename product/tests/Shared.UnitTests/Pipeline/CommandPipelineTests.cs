using System.Diagnostics;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Application.Auditing;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Application.Idempotency;
using EventTicketing.Shared.Application.Transactions;
using EventTicketing.Shared.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EventTicketing.Shared.UnitTests.Pipeline;

/// <summary>The decorator pipeline from ADR-0033: order, short-circuits and fail-closed authorization.</summary>
public sealed class CommandPipelineTests : IDisposable
{
    private readonly Steps _steps = new();
    private readonly ActivityListener _listener;

    public CommandPipelineTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CommandTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => _steps.Add("tracing start"),
            ActivityStopped = _ => _steps.Add("tracing stop"),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task Runs_every_step_in_the_agreed_order()
    {
        var result = await Send(new Scenario { IdempotencyKey = "key-1" }, new SampleCommand("Ana"));

        result.Value.Should().Be("hello Ana");
        _steps.Items.Should().Equal(
            "context",
            "authorization",
            "validation",
            "idempotency claim",
            "transaction begin",
            "tracing start",
            "handler",
            "tracing stop",
            "audit",
            "transaction commit",
            "idempotency complete");
    }

    [Fact]
    public async Task Stops_an_invalid_command_before_it_reaches_the_handler()
    {
        var result = await Send(new Scenario(), new SampleCommand(""));

        result.Error.Should().BeOfType<ValidationError>()
            .Which.Errors.Should().ContainSingle(error => error.Field == nameof(SampleCommand.Name));
        _steps.Items.Should().Equal("context", "authorization", "validation");
    }

    [Fact]
    public async Task Refuses_a_caller_the_authorizer_rejects()
    {
        var result = await Send(new Scenario { AllowCaller = false }, new SampleCommand("Ana"));

        result.Error.Type.Should().Be(ErrorType.Forbidden);
        result.Error.Code.Should().Be("authorization.denied");
        _steps.Items.Should().Equal("context", "authorization");
    }

    [Fact]
    public async Task Refuses_a_command_that_has_no_authorizer()
    {
        await using var services = BuildServices(new Scenario());
        await using var scope = services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<UnguardedCommand, Unit>>();

        var result = await handler.HandleAsync(new UnguardedCommand(), TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("authorization.not_configured");
        _steps.Items.Should().NotContain("unguarded handler");
    }

    [Fact]
    public async Task Replays_a_completed_idempotency_key_without_running_the_handler()
    {
        var scenario = new Scenario
        {
            IdempotencyKey = "key-1",
            PreviousResult = new StoredResult(true, "\"hello from the first request\"", null, null, null),
        };

        var result = await Send(scenario, new SampleCommand("Ana"));

        result.Value.Should().Be("hello from the first request");
        _steps.Items.Should().NotContain("handler");
    }

    [Fact]
    public async Task Does_not_use_the_idempotency_store_when_the_caller_sent_no_key()
    {
        await Send(new Scenario(), new SampleCommand("Ana"));

        _steps.Items.Should().NotContain(step => step.StartsWith("idempotency", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rolls_back_and_does_not_audit_a_failed_command()
    {
        var result = await Send(new Scenario { HandlerFails = true, IdempotencyKey = "key-1" }, new SampleCommand("Ana"));

        result.Error.Code.Should().Be("sample.conflict");
        _steps.Items.Should().Contain("transaction rollback").And.NotContain("audit");
        _steps.Items.Should().EndWith("idempotency complete", "a business failure is stored and replayed too");
    }

    private async Task<Result<string>> Send(Scenario scenario, SampleCommand command)
    {
        await using var services = BuildServices(scenario);
        await using var scope = services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<SampleCommand, string>>();

        return await handler.HandleAsync(command, TestContext.Current.CancellationToken);
    }

    private ServiceProvider BuildServices(Scenario scenario)
    {
        var services = new ServiceCollection();

        services.AddSingleton(_steps);
        services.AddSingleton(scenario);
        services.AddLogging(logging => logging.AddProvider(new ScopeRecordingLoggerProvider(_steps)));
        services.AddScoped<IExecutionContext, FakeExecutionContext>();
        services.AddSingleton<IIdempotencyStore, FakeIdempotencyStore>();
        services.AddSingleton<ITransactionRunner, FakeTransactionRunner>();
        services.AddSingleton<IAuditLog, FakeAuditLog>();

        services.AddCommandHandlers(typeof(CommandPipelineTests).Assembly);
        services.AddCommandPipeline();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
