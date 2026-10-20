using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>
/// M1b G2 (ADR-0027): Organizers is the source of truth on membership and keeps Keycloak in step. A
/// member added through the API gets the organizer's ID in their next token; once removed, not any more.
/// </summary>
public sealed class OrganizerMembershipTests(RunningApp running)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_member_added_in_Organizers_finds_the_organizer_in_their_next_token()
    {
        using var adam = running.Client("backofficeapi", token: await running.TokenAsync("adam", Ct));

        // A new organizer through the API: its group appears in Keycloak.
        var created = await adam.PostAsJsonAsync(new Uri("/organizers", UriKind.Relative), new { name = "Membership test " + Guid.NewGuid().ToString("N")[..6] }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var organizerId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("organizerId").GetString()!;

        var gusId = (await MeAsync("gus")).GetProperty("userId").GetString()!;
        (await adam.PostAsJsonAsync(new Uri($"/organizers/{organizerId}/members", UriKind.Relative), new { userId = gusId, displayName = "Gus" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await RunningApp.EventuallyAsync(() => OrganizerIdsOfAsync("gus"), ids => ids.Contains(organizerId), DemoScript.AsyncStep, Ct);

        (await adam.DeleteAsync(new Uri($"/organizers/{organizerId}/members/{gusId}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await RunningApp.EventuallyAsync(() => OrganizerIdsOfAsync("gus"), ids => !ids.Contains(organizerId), DemoScript.AsyncStep, Ct);
    }

    private async Task<JsonElement> MeAsync(string user)
    {
        using var client = running.Client("publicapi", token: await running.TokenAsync(user, Ct));
        return await client.GetFromJsonAsync<JsonElement>(new Uri("/me", UriKind.Relative), Ct);
    }

    /// <summary>A fresh sign-in each time: membership shows in the next token (ADR-0027).</summary>
    private async Task<IReadOnlyList<string>> OrganizerIdsOfAsync(string user) =>
        [.. (await MeAsync(user)).GetProperty("organizerIds").EnumerateArray().Select(id => id.GetString()!)];
}
