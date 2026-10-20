using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using Aspire.Hosting.Testing;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>
/// A fan in a browser, over HTTP: signs in to the fan site through Keycloak's real login form and
/// then fetches pages as that fan (prerendered HTML).
/// </summary>
internal sealed partial class FanSiteBrowser : IDisposable
{
    private readonly HttpClient _http;

    private FanSiteBrowser(Uri fanSite, HttpClient http)
    {
        FanSite = fanSite;
        _http = http;
    }

    public Uri FanSite { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The sign-in round trip, checked on the way: the fan site pushes the authorization request to
    /// Keycloak (PAR), which checks the client and its redirect URI and sends the fan to the login
    /// page with only a reference to it. The realm requires PKCE (S256) for this client, so a
    /// successful sign-in also proves PKCE; the code exchange proves the generated client secret.
    /// </summary>
    public static async Task<FanSiteBrowser> SignInAsync(RunningApp running, string user)
    {
        var fanSite = running.App.GetEndpoint("fansite", "http");
        var http = new HttpClient(new LocalhostBrowserCookies { InnerHandler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false } });

        var challenge = await http.GetAsync(new Uri(fanSite, "/account/login?returnUrl=%2F"), Ct);
        challenge.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var authorize = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        authorize["client_id"].Should().Be("eventticketing-fansite");
        authorize["request_uri"].Should().StartWith("urn:ietf:params:oauth:request_uri:");

        // Keycloak's login form (after its own redirects), as a browser would fill it in.
        var loginPage = await http.GetAsync(challenge.Headers.Location, Ct);
        while (loginPage.StatusCode == HttpStatusCode.Found)
        {
            loginPage = await http.GetAsync(new Uri(challenge.Headers.Location!, loginPage.Headers.Location!), Ct);
        }

        var loginHtml = await loginPage.Content.ReadAsStringAsync(Ct);
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK, "Keycloak accepts the client and its redirect URI: {0}", loginHtml);
        var form = LoginForm().Match(loginHtml).Value;
        var formAction = WebUtility.HtmlDecode(FormAction().Match(form).Groups["url"].Value);
        using var credentials = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user, ["password"] = user });
        var loggedIn = await http.PostAsync(new Uri(formAction), credentials, Ct);
        loggedIn.StatusCode.Should().Be(HttpStatusCode.Found);

        // Back at the fan site, which exchanges the code with its client secret.
        var callback = await http.GetAsync(loggedIn.Headers.Location, Ct);
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect, "the code exchange succeeds");
        callback.Headers.Location!.OriginalString.Should().Be("/");

        return new FanSiteBrowser(fanSite, http);
    }

    /// <summary>A page as the signed-in fan sees it before the circuit starts: the prerendered HTML.</summary>
    public Task<string> PageAsync(string path) => _http.GetStringAsync(new Uri(FanSite, path), Ct);

    public void Dispose() => _http.Dispose();

    [GeneratedRegex("""<form[^>]*id="kc-form-login"[^>]*>""")]
    private static partial Regex LoginForm();

    [GeneratedRegex("""action="(?<url>[^"]+)""")]
    private static partial Regex FormAction();

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
}
