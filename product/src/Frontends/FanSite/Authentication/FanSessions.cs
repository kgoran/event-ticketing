using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace EventTicketing.FanSite.Authentication;

/// <summary>Links the sign-in cookie to the tokens <see cref="FanTokenStore"/> keeps on the server.</summary>
internal static class FanSessions
{
    /// <summary>The cookie's only link to the tokens: a random key, meaningless outside this site.</summary>
    public const string SessionClaim = "fansite_session";

    /// <summary>HttpContext item for the ID token while signing out (<see cref="EndAtIdentityProviderAsync"/>).</summary>
    public const string IdTokenItem = "fansite:id_token";

    /// <summary>After the code exchange: keep the tokens on the server and put only the session key in the cookie.</summary>
    public static async Task StartAsync(TokenValidatedContext context)
    {
        var response = context.TokenEndpointResponse
            ?? throw new InvalidOperationException("The authorization code flow always has a token response.");
        var services = context.HttpContext.RequestServices;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        await services.GetRequiredService<FanTokenStore>().SaveAsync(
            sessionId,
            FanTokens.From(response, now),
            FanTokens.SessionLifetime(response),
            context.HttpContext.RequestAborted);

        ((ClaimsIdentity)context.Principal!.Identity!).AddClaim(new Claim(SessionClaim, sessionId));
    }

    /// <summary>
    /// On every request with the cookie: if its tokens are gone (signed out, refresh refused, the
    /// site restarted), the fan is signed out here too instead of looking signed in without access.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var store = context.HttpContext.RequestServices.GetRequiredService<FanTokenStore>();
        if (context.Principal?.FindFirstValue(SessionClaim) is { } sessionId
            && await store.GetAsync(sessionId, context.HttpContext.RequestAborted) is not null)
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>Signing out at the identity provider too; the ID token tells it which session, so it doesn't ask.</summary>
    public static Task EndAtIdentityProviderAsync(RedirectContext context)
    {
        if (context.HttpContext.Items[IdTokenItem] is string idToken)
        {
            context.ProtocolMessage.IdTokenHint = idToken;
        }

        return Task.CompletedTask;
    }
}
