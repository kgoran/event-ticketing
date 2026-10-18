using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>
/// The whole system under Aspire: every resource comes up healthy (M1a walking skeleton, S1), and
/// a token from the real Keycloak realm is accepted by the APIs (K1).
/// </summary>
public sealed class WalkingSkeletonTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Starts_every_resource_and_the_apis_accept_tokens_from_the_realm()
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
        (await publicApi.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await publicApi.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await publicApi.GetAsync(new Uri("/me", UriKind.Relative), cancellationToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var keycloak = app.CreateHttpClient("keycloak", "http");

        // Olga is a member of the seeded organizer: the token carries her role and organizer ID.
        publicApi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(keycloak, "olga", cancellationToken));
        var olga = await publicApi.GetFromJsonAsync<JsonElement>(new Uri("/me", UriKind.Relative), cancellationToken);
        Strings(olga, "roles").Should().Contain("organizer");
        Strings(olga, "organizerIds").Should().Equal("0199f2a0-0000-7000-8000-000000000001");

        // Ana only has the default role, which makes every user a fan.
        publicApi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(keycloak, "ana", cancellationToken));
        var ana = await publicApi.GetFromJsonAsync<JsonElement>(new Uri("/me", UriKind.Relative), cancellationToken);
        Strings(ana, "roles").Should().Contain("fan");
        Strings(ana, "organizerIds").Should().BeEmpty();
    }

    /// <summary>Password grant with the development-only client, as the .http files and the seeder do.</summary>
    private static async Task<string> GetTokenAsync(HttpClient keycloak, string user, CancellationToken cancellationToken)
    {
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

    private static IEnumerable<string?> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(value => value.GetString());
}
