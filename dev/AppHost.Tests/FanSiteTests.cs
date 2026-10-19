using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using Aspire.Hosting.Testing;

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
        var fanSite = running.App.GetEndpoint("fansite", "http");
        using var browser = new HttpClient(new LocalhostBrowserCookies { InnerHandler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false } });

        // The fan site pushes the authorization request to Keycloak (PAR), which checks the client
        // and its redirect URI, then sends the fan to the login page with only a reference to it.
        // The realm requires PKCE (S256) for this client, so signing in below also proves PKCE.
        var challenge = await browser.GetAsync(new Uri(fanSite, "/account/login?returnUrl=%2F"), Ct);
        challenge.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var authorize = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        authorize["client_id"].Should().Be("eventticketing-fansite");
        authorize["request_uri"].Should().StartWith("urn:ietf:params:oauth:request_uri:");

        // Keycloak's login form (after its own redirects), as a browser would fill it in.
        var loginPage = await browser.GetAsync(challenge.Headers.Location, Ct);
        while (loginPage.StatusCode == HttpStatusCode.Found)
        {
            loginPage = await browser.GetAsync(new Uri(challenge.Headers.Location!, loginPage.Headers.Location!), Ct);
        }

        var loginHtml = await loginPage.Content.ReadAsStringAsync(Ct);
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK, "Keycloak accepts the client and its redirect URI: {0}", loginHtml);
        var form = LoginForm().Match(loginHtml).Value;
        var formAction = WebUtility.HtmlDecode(FormAction().Match(form).Groups["url"].Value);
        using var credentials = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "ana", ["password"] = "ana" });
        var loggedIn = await browser.PostAsync(new Uri(formAction), credentials, Ct);
        loggedIn.StatusCode.Should().Be(HttpStatusCode.Found);

        // Back at the fan site, which exchanges the code with its generated client secret.
        var callback = await browser.GetAsync(loggedIn.Headers.Location, Ct);
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect, "the code exchange succeeds");
        callback.Headers.Location!.OriginalString.Should().Be("/");

        var home = await browser.GetStringAsync(new Uri(fanSite, "/"), Ct);
        home.Should().Contain("Signed in as").And.Contain(">ana<");
    }

    /// <summary>
    /// Cookies as a browser keeps them for localhost. Browsers treat http://localhost as secure, so
    /// they send Keycloak's Secure cookies over plain HTTP; .NET's CookieContainer doesn't. All
    /// cookies are kept by name: Keycloak's and the fan site's names don't overlap.
    /// </summary>
    private sealed class LocalhostBrowserCookies : DelegatingHandler
    {
        private readonly Dictionary<string, string> _cookies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
            }

            var response = await base.SendAsync(request, cancellationToken);
            foreach (var setCookie in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
            {
                var nameAndValue = setCookie.Split(';', 2)[0].Split('=', 2);
                var (name, value) = (nameAndValue[0], nameAndValue[1]);
                var deleted = value.Length == 0
                    || setCookie.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase)
                    || setCookie.Contains("1970", StringComparison.Ordinal);
                if (deleted)
                {
                    _cookies.Remove(name);
                }
                else
                {
                    _cookies[name] = value;
                }
            }

            return response;
        }
    }

    [GeneratedRegex("events/[0-9a-f-]{36}")]
    private static partial Regex EventLink();

    [GeneratedRegex("""<form[^>]*id="kc-form-login"[^>]*>""")]
    private static partial Regex LoginForm();

    [GeneratedRegex("""action="(?<url>[^"]+)""")]
    private static partial Regex FormAction();
}
