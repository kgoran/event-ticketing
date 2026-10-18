using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>M1a walking skeleton (S1): starting the AppHost brings everything up healthy.</summary>
public sealed class WalkingSkeletonTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Starts_every_resource_and_every_host_reports_healthy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        var migrator = await app.ResourceNotifications
            .WaitForResourceAsync("migrator", e => e.Snapshot.State?.Text == KnownResourceStates.Finished, cancellationToken)
            .WaitAsync(StartupTimeout, cancellationToken);
        migrator.Snapshot.ExitCode.Should().Be(0, "the Migrator must finish successfully before the hosts start");

        foreach (var resource in new[] { "postgres", "keycloak", "localpay", "publicapi", "backofficeapi", "webhookingress", "workers" })
        {
            await app.ResourceNotifications
                .WaitForResourceHealthyAsync(resource, cancellationToken)
                .WaitAsync(StartupTimeout, cancellationToken);
        }

        using var publicApi = app.CreateHttpClient("publicapi");
        var health = await publicApi.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);
        health.StatusCode.Should().Be(HttpStatusCode.OK);

        var openApi = await publicApi.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), cancellationToken);
        openApi.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
