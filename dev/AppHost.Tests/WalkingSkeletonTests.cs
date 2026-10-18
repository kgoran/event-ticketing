using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>Every resource is up (M1a S1) and the APIs accept tokens from the real realm (K1).</summary>
public sealed class WalkingSkeletonTests(RunningApp running)
{
    [Fact]
    public async Task The_apis_are_healthy_and_accept_tokens_from_the_realm()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var anonymous = running.Client("publicapi");
        (await anonymous.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync(new Uri("/me", UriKind.Relative), cancellationToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Olga is a member of the seeded organizer: the token carries her role and organizer ID.
        using var olga = running.Client("publicapi", token: await running.TokenAsync("olga", cancellationToken));
        var olgaCaller = await olga.GetFromJsonAsync<JsonElement>(new Uri("/me", UriKind.Relative), cancellationToken);
        Strings(olgaCaller, "roles").Should().Contain("organizer");
        Strings(olgaCaller, "organizerIds").Should().Equal(SeededOrganizer.Id.ToString());

        // Ana only has the default role, which makes every user a fan.
        using var ana = running.Client("publicapi", token: await running.TokenAsync("ana", cancellationToken));
        var anaCaller = await ana.GetFromJsonAsync<JsonElement>(new Uri("/me", UriKind.Relative), cancellationToken);
        Strings(anaCaller, "roles").Should().Contain("fan");
        Strings(anaCaller, "organizerIds").Should().BeEmpty();
    }

    private static IEnumerable<string?> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(value => value.GetString());
}

internal static class SeededOrganizer
{
    public static readonly Guid Id = Guid.Parse("0199f2a0-0000-7000-8000-000000000001");
}
