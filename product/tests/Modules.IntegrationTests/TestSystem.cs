using System.Net.Http.Headers;
using EventTicketing.BackOfficeApi;
using EventTicketing.Catalog;
using EventTicketing.Inventory.Infrastructure;
using EventTicketing.Notifications;
using EventTicketing.Notifications.Email;
using EventTicketing.Orders.Infrastructure;
using EventTicketing.Organizers;
using EventTicketing.Payments.Infrastructure;
using EventTicketing.PublicApi;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Shared.Infrastructure.Scheduling;
using EventTicketing.Testing;
using EventTicketing.Ticketing.Infrastructure;
using EventTicketing.WebhookIngress;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(EventTicketing.Modules.IntegrationTests.TestSystem))]

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// The product's hosts in memory against one migrated PostgreSQL database, started once for the
/// test run. PublicApi and BackOfficeApi take HTTP requests with test tokens; WebhookIngress takes
/// webhooks; every outgoing HTTP call goes to <see cref="LocalPay"/>. The Workers host is
/// built but never started: <see cref="DeliverMessagesAsync"/> runs the outbox relay and the
/// scheduler one pass at a time, so tests decide when messages move. Tests share the database and
/// stay apart by using their own organizers, venues and events.
/// </summary>
public sealed class TestSystem : IAsyncLifetime
{
    private const string ConnectionSetting = "ConnectionStrings:" + ModuleDatabaseRegistration.ConnectionName;

    /// <summary>The secret the events' private keys are encrypted under (ADR-0051), as the AppHost generates one.</summary>
    internal const string MasterKey = "test-master-key-at-least-32-characters-long";

    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=500")
        .Build();

    private WebApplicationFactory<PublicApiEntryPoint>? _publicApi;
    private WebApplicationFactory<BackOfficeApiEntryPoint>? _backOfficeApi;
    private WebApplicationFactory<WebhookIngressEntryPoint>? _webhookIngress;
    private IHost? _workers;

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The payment provider every host talks to.</summary>
    internal FakeLocalPay LocalPay { get; } = new();

    /// <summary>The SMTP server Notifications sends to (ADR-0052): records every email.</summary>
    internal FakeMailServer Mail { get; } = new();

    /// <summary>The identity provider's groups Organizers keeps in step (ADR-0027).</summary>
    internal FakeIdentityDirectory Directory { get; } = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        ConnectionString = _postgres.GetConnectionString();

        await MigrateAsync();

        _publicApi = InMemory(new WebApplicationFactory<PublicApiEntryPoint>());
        _backOfficeApi = InMemory(new WebApplicationFactory<BackOfficeApiEntryPoint>());
        _webhookIngress = InMemory(new WebApplicationFactory<WebhookIngressEntryPoint>());
        _workers = BuildHost(HostKind.Workers);
    }

    public async ValueTask DisposeAsync()
    {
        _workers?.Dispose();
        if (_publicApi is not null)
        {
            await _publicApi.DisposeAsync();
        }

        if (_backOfficeApi is not null)
        {
            await _backOfficeApi.DisposeAsync();
        }

        if (_webhookIngress is not null)
        {
            await _webhookIngress.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public HttpClient PublicApi(string? token = null) => WithToken(Started(_publicApi).CreateClient(), token);

    /// <summary>
    /// A PublicApi of its own, on the same database, with extra settings (a feature flag switched on):
    /// a test that changes how the host behaves doesn't change it for the tests running beside it.
    /// </summary>
    public WebApplicationFactory<PublicApiEntryPoint> PublicApiWith(params (string Key, string Value)[] settings) =>
        InMemory(new WebApplicationFactory<PublicApiEntryPoint>()).WithWebHostBuilder(host =>
        {
            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }
        });

    public static HttpClient Client<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory, string? token = null)
        where TEntryPoint : class =>
        WithToken(factory.CreateClient(), token);

    public HttpClient BackOfficeApi(string? token = null) => WithToken(Started(_backOfficeApi).CreateClient(), token);

    public HttpClient WebhookIngress() => Started(_webhookIngress).CreateClient();

    /// <summary>
    /// What the Workers host does in the background, until there is nothing left: relays every
    /// module's outbox to its consumers and runs scheduled messages that are due. Returns how many
    /// messages moved.
    /// </summary>
    public async Task<int> DeliverMessagesAsync()
    {
        var workers = _workers ?? throw new InvalidOperationException("The system has not started.");
        var modules = workers.Services.GetRequiredService<ModuleRegistry>().Modules;
        var deadline = DateTime.UtcNow + DeliveryTimeout;

        var total = 0;
        while (true)
        {
            var moved = 0;
            foreach (var module in modules)
            {
                await using var scope = workers.Services.CreateAsyncScope();
                moved += await scope.ServiceProvider.GetRequiredService<OutboxRelay>().RunOnceAsync(module, CancellationToken);
                moved += await scope.ServiceProvider.GetRequiredService<ScheduledMessageRunner>().RunOnceAsync(module, CancellationToken);
            }

            total += moved;
            if (moved > 0)
            {
                continue;
            }

            // Tests run in parallel on one database: another test's pass may have leased a message
            // this test is waiting for and still be delivering it. Done only when nothing due is left.
            if (await PendingMessagesAsync(modules) == 0)
            {
                return total;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Messages were still pending after {DeliveryTimeout.TotalSeconds:0} s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken);
        }
    }

    /// <summary>Runs <paramref name="action"/> in a scope of the Workers host, as a message handler would: no signed-in caller.</summary>
    public async Task<T> InWorkersAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var workers = _workers ?? throw new InvalidOperationException("The system has not started.");
        await using var scope = workers.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Runs a query whose parameters are named @p0, @p1, ... and returns the first column of each row.</summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i]);
        }

        var values = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        while (await reader.ReadAsync(CancellationToken))
        {
            values.Add(reader.GetFieldValue<T>(0));
        }

        return values;
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters) =>
        (await QueryAsync<T>(sql, parameters)).Single();

    /// <summary>Outbox and scheduled messages that are due and not yet done, in every module.</summary>
    private async Task<long> PendingMessagesAsync(IReadOnlyList<ModuleDatabase> modules)
    {
        long pending = 0;
        foreach (var module in modules)
        {
            // Schema names come from AddModuleDatabase, which accepts only identifiers.
            pending += await ScalarAsync<long>(
                $"SELECT (SELECT count(*) FROM {module.Schema}.outbox_messages " +
                "WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND next_attempt_at <= now()) " +
                $"+ (SELECT count(*) FROM {module.Schema}.scheduled_messages WHERE dead_lettered_at IS NULL AND due_at <= now())");
        }

        return pending;
    }

    private static T Started<T>(T? value)
        where T : class =>
        value ?? throw new InvalidOperationException("The system has not started.");

    private static HttpClient WithToken(HttpClient client, string? token)
    {
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static IModule[] Modules() => [new CatalogModule(), new InventoryModule(), new OrdersModule(), new PaymentsModule(), new OrganizersModule(), new TicketingModule(), new NotificationsModule()];

    private WebApplicationFactory<TEntryPoint> InMemory<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class =>
        factory.WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.UseSetting(ConnectionSetting, ConnectionString);
            foreach (var (key, value) in Settings)
            {
                host.UseSetting(key, value);
            }

            host.ConfigureTestServices(services =>
            {
                services.UseTestTokens();
                services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(LocalPay.CreateHandler));
            });
        });

    /// <summary>Configuration every host gets, as the AppHost would give it.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Settings =>
    [
        new("Payments:LocalPay:BaseUrl", FakeLocalPay.BaseUrl),
        new("Payments:LocalPay:WebhookSecret", FakeLocalPay.WebhookSecret),
        new("Checkout:ReturnUrlTemplate", "http://publicapi.test/orders/{orderId}"),
        new("Ticketing:MasterKey", MasterKey),
        new("Email:FanSiteUrl", "http://fansite.test"),

        // The sweeper runs only when a test makes it due (Scenarios.SweepNowAsync): a sweep briefly
        // locks expired seats, and a claim running into that lock skips the seat (ADR-0011), which
        // would make other tests' claims on just-expired seats fail at random.
        new("Inventory:SweepInterval", "01:00:00"),
    ];

    private IHost BuildHost(HostKind kind)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration[ConnectionSetting] = ConnectionString;
        foreach (var (key, value) in Settings)
        {
            builder.Configuration[key] = value;
        }

        builder.AddModules(kind, Modules());
        builder.Services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(LocalPay.CreateHandler));
        builder.Services.AddSingleton<IEmailSender>(Mail);
        builder.Services.AddSingleton<EventTicketing.Organizers.Identity.IIdentityDirectory>(Directory);
        return builder.Build();
    }

    /// <summary>What the Migrator host does: every module's EF Core migrations, in order.</summary>
    private async Task MigrateAsync()
    {
        using var migrator = BuildHost(HostKind.Migrator);
        await using var scope = migrator.Services.CreateAsyncScope();
        foreach (var migration in scope.ServiceProvider.GetServices<IModuleMigration>())
        {
            await migration.MigrateAsync(CancellationToken);
        }
    }
}
