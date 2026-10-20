using System.Text.RegularExpressions;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>
/// M1a U1: the fan site works against the real PublicApi and Keycloak (ADR-0049). Checked over
/// HTTP: the prerendered pages, and the sign-in round trip through Keycloak's login form.
/// </summary>
public sealed partial class FanSiteTests(RunningApp running)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anyone_can_browse_the_seeded_events_on_the_fan_site()
    {
        (await running.SeedAsync(Ct)).Should().Be(0);
        using var browser = running.Client("fansite");

        var home = await browser.GetStringAsync(new Uri("/", UriKind.Relative), Ct);
        home.Should().Contain("Demo Concert").And.Contain("Failure Scenarios").And.Contain("Sign in");

        var eventPath = EventLink().Match(home).Value;
        var eventPage = await browser.GetStringAsync(new Uri(eventPath, UriKind.Relative), Ct);
        eventPage.Should().Contain("Demo Arena").And.Contain("On sale");
    }

    [Fact]
    public async Task A_fan_signs_in_through_keycloak_and_is_signed_in_on_the_fan_site()
    {
        using var ana = await FanSiteBrowser.SignInAsync(running, "ana");

        (await ana.PageAsync("/")).Should().Contain("Signed in as").And.Contain(">ana<");
    }

    [GeneratedRegex("events/[0-9a-f-]{36}")]
    private static partial Regex EventLink();
}
