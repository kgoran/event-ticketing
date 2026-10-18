using EventTicketing.Shared.Application.Auditing;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Application.Idempotency;
using EventTicketing.Shared.Application.Transactions;
using EventTicketing.Shared.Domain;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;

namespace EventTicketing.Shared.UnitTests.Pipeline;

// Test doubles for the command pipeline. Each one records the step it represents, so a test can
// assert the order in which the decorators run.

internal sealed class Steps
{
    private readonly List<string> _items = [];

    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_items)
            {
                return [.. _items];
            }
        }
    }

    public void Add(string step)
    {
        lock (_items)
        {
            _items.Add(step);
        }
    }
}

internal sealed class Scenario
{
    public bool AllowCaller { get; init; } = true;

    public bool HandlerFails { get; init; }

    public string? IdempotencyKey { get; init; }

    public StoredResult? PreviousResult { get; init; }
}

internal sealed record SampleCommand(string Name) : ICommand<string>;

internal sealed class SampleHandler(Steps steps, Scenario scenario) : ICommandHandler<SampleCommand, string>
{
    public Task<Result<string>> HandleAsync(SampleCommand command, CancellationToken cancellationToken)
    {
        steps.Add("handler");

        Result<string> result = scenario.HandlerFails
            ? Error.Conflict("sample.conflict", "Something conflicting happened.")
            : $"hello {command.Name}";

        return Task.FromResult(result);
    }
}

internal sealed class SampleAuthorizer(Steps steps, Scenario scenario) : ICommandAuthorizer<SampleCommand>
{
    public ValueTask<bool> IsAllowedAsync(SampleCommand command, IExecutionContext context, CancellationToken cancellationToken)
    {
        steps.Add("authorization");
        return ValueTask.FromResult(scenario.AllowCaller);
    }
}

internal sealed class SampleValidator : AbstractValidator<SampleCommand>
{
    private readonly Steps _steps;

    public SampleValidator(Steps steps)
    {
        _steps = steps;
        RuleFor(command => command.Name).NotEmpty();
    }

    public override Task<ValidationResult> ValidateAsync(
        ValidationContext<SampleCommand> context,
        CancellationToken cancellation = default)
    {
        _steps.Add("validation");
        return base.ValidateAsync(context, cancellation);
    }
}

/// <summary>A coordinating command: it calls other commands and must not hold a transaction open.</summary>
internal sealed record CoordinatingCommand : ICommand<string>, INonTransactionalCommand;

internal sealed class CoordinatingHandler(Steps steps) : ICommandHandler<CoordinatingCommand, string>
{
    public Task<Result<string>> HandleAsync(CoordinatingCommand command, CancellationToken cancellationToken)
    {
        steps.Add("coordinator");
        return Task.FromResult<Result<string>>("coordinated");
    }
}

internal sealed class CoordinatingAuthorizer : InternalCommandAuthorizer<CoordinatingCommand>;

/// <summary>A command nobody wrote an authorizer for.</summary>
internal sealed record UnguardedCommand : ICommand<Unit>;

internal sealed class UnguardedHandler(Steps steps) : ICommandHandler<UnguardedCommand, Unit>
{
    public Task<Result<Unit>> HandleAsync(UnguardedCommand command, CancellationToken cancellationToken)
    {
        steps.Add("unguarded handler");
        return Task.FromResult<Result<Unit>>(Unit.Value);
    }
}

internal sealed class FakeExecutionContext(Scenario scenario) : IExecutionContext
{
    public string? UserId => "user-1";

    public bool IsAuthenticated => true;

    public IReadOnlySet<string> Roles { get; } = new HashSet<string> { "fan" };

    public IReadOnlySet<Guid> OrganizerIds { get; } = new HashSet<Guid>();

    public string? IdempotencyKey => scenario.IdempotencyKey;
}

internal sealed class FakeIdempotencyStore(Steps steps, Scenario scenario) : IIdempotencyStore
{
    public Task<IdempotencyClaim> TryClaimAsync(string key, Type commandType, CancellationToken cancellationToken)
    {
        steps.Add("idempotency claim");
        IdempotencyClaim claim = scenario.PreviousResult is { } previous
            ? new IdempotencyClaim.AlreadyCompleted(previous)
            : new IdempotencyClaim.Claimed();
        return Task.FromResult(claim);
    }

    public Task CompleteAsync(string key, Type commandType, StoredResult result, CancellationToken cancellationToken)
    {
        steps.Add("idempotency complete");
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, Type commandType, CancellationToken cancellationToken)
    {
        steps.Add("idempotency release");
        return Task.CompletedTask;
    }
}

internal sealed class FakeTransactionRunner(Steps steps) : ITransactionRunner
{
    public async Task<Result<TResult>> RunAsync<TResult>(
        Type commandType,
        Func<CancellationToken, Task<Result<TResult>>> work,
        CancellationToken cancellationToken)
    {
        steps.Add("transaction begin");
        var result = await work(cancellationToken);
        steps.Add(result.IsSuccess ? "transaction commit" : "transaction rollback");
        return result;
    }
}

internal sealed class FakeAuditLog(Steps steps) : IAuditLog
{
    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        steps.Add("audit");
        return Task.CompletedTask;
    }
}

/// <summary>Records when the context decorator opens its logging scope.</summary>
internal sealed class ScopeRecordingLoggerProvider(Steps steps) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ScopeRecordingLogger(steps);

    public void Dispose()
    {
    }

    private sealed class ScopeRecordingLogger(Steps steps) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values && values.Any(pair => pair.Key == "Command"))
            {
                steps.Add("context");
            }

            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
