using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

[assembly: AssemblyFixture(typeof(EventTicketing.Dev.AppHost.Tests.RunningApp))]

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>The whole system under Aspire, started once for all system tests.</summary>
public sealed class RunningApp : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    private DistributedApplication? _app;

    public DistributedApplication App => _app ?? throw new InvalidOperationException("The app has not started.");

    public async ValueTask InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(StartupTimeout);
        var cancellationToken = timeout.Token;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(cancellationToken);
        _app = await appHost.BuildAsync(cancellationToken);
        await _app.StartAsync(cancellationToken);

        var migrator = await _app.ResourceNotifications
            .WaitForResourceAsync("migrator", e => e.Snapshot.State?.Text == KnownResourceStates.Finished, cancellationToken);
        if (migrator.Snapshot.ExitCode != 0)
        {
            throw new InvalidOperationException($"The Migrator exited with code {migrator.Snapshot.ExitCode}.");
        }

        foreach (var resource in new[] { "postgres", "keycloak", "localpay", "publicapi", "backofficeapi", "webhookingress", "workers" })
        {
            await _app.ResourceNotifications.WaitForResourceHealthyAsync(resource, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    public HttpClient Client(string resource, string? token = null)
    {
        var client = App.CreateHttpClient(resource);
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    /// <summary>Password grant with the development-only client, as the .http files and the seeder do.</summary>
    public async Task<string> TokenAsync(string user, CancellationToken cancellationToken)
    {
        using var keycloak = App.CreateHttpClient("keycloak", "http");
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "eventticketing-dev",
            ["username"] = user,
            ["password"] = user,
        });

        using var response = await keycloak.PostAsync(
            new Uri("/realms/eventticketing/protocol/openid-connect/token", UriKind.Relative), request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>Polls <paramref name="probe"/> until <paramref name="isDone"/>, for asynchronous steps like the outbox.</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> probe, Func<T, bool> isDone, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var value = await probe();
            if (isDone(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Gave up after {timeout.TotalSeconds:0} s; last value: {value}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }
}
