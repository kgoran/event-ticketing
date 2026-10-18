using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Context;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Shared.Infrastructure.Scheduling;
using EventTicketing.Shared.IntegrationTests.Sales;
using EventTicketing.Shared.IntegrationTests.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace EventTicketing.Shared.IntegrationTests;

/// <summary>
/// A Workers host with the two test modules, built but never started: tests drive the relay and
/// the scheduler one pass at a time, so every step is deterministic.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _connectionString;

    private TestApp(IHost host, string connectionString)
    {
        _host = host;
        _connectionString = connectionString;
    }

    public TestFaults Faults => _host.Services.GetRequiredService<TestFaults>();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static async Task<TestApp> CreateAsync(
        string connectionString,
        Action<MessagingOptions>? configure = null,
        bool createTables = true,
        Action<IServiceCollection>? services = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration["ConnectionStrings:" + ModuleDatabaseRegistration.ConnectionName] = connectionString;
        builder.Services.AddSingleton<TestFaults>();
        builder.AddModules(HostKind.Workers, new SalesModule(), new WarehouseModule());

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        services?.Invoke(builder.Services);

        var app = new TestApp(builder.Build(), connectionString);
        if (createTables)
        {
            await app.CreateTablesAsync();
        }

        return app;
    }

    public async Task<Result<TResult>> SendAsync<TCommand, TResult>(TCommand command, string? idempotencyKey = null)
        where TCommand : ICommand<TResult>
    {
        await using var scope = _host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<MessageExecutionContext>().IdempotencyKey = idempotencyKey;

        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>();
        return await handler.HandleAsync(command, CancellationToken);
    }

    public Task<Result<Guid>> PlaceOrderAsync(string customer, bool reject = false, string? idempotencyKey = null) =>
        SendAsync<PlaceOrder, Guid>(new PlaceOrder(customer, reject), idempotencyKey);

    /// <summary>One relay pass over the Sales outbox; returns how many messages it claimed.</summary>
    public async Task<int> RelayOnceAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var module = scope.ServiceProvider.GetRequiredService<ModuleRegistry>().BySchema("sales");
        return await scope.ServiceProvider.GetRequiredService<OutboxRelay>().RunOnceAsync(module, CancellationToken);
    }

    public async Task<int> RelayUntilEmptyAsync()
    {
        var total = 0;
        int claimed;
        while ((claimed = await RelayOnceAsync()) > 0)
        {
            total += claimed;
        }

        return total;
    }

    /// <summary>One scheduler pass over the Sales module; returns how many messages it claimed.</summary>
    public async Task<int> SchedulerOnceAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var module = scope.ServiceProvider.GetRequiredService<ModuleRegistry>().BySchema("sales");
        return await scope.ServiceProvider.GetRequiredService<ScheduledMessageRunner>().RunOnceAsync(module, CancellationToken);
    }

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public async Task<long> CountAsync(string table, string? where = null) =>
        await ScalarAsync<long>($"SELECT count(*) FROM {table}" + (where is null ? string.Empty : $" WHERE {where}"));

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
    }

    private async Task CreateTablesAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        foreach (var module in scope.ServiceProvider.GetRequiredService<ModuleRegistry>().Modules)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(module.ContextType);
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(CancellationToken);
        }
    }
}
