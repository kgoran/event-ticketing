using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace EventTicketing.FanSite.Authentication;

/// <summary>
/// The signed-in fan's access token for PublicApi, refreshed when it is about to expire. Scoped to
/// the circuit (or, while prerendering, the request), and it finds the fan through
/// <see cref="AuthenticationStateProvider"/>, which works in both; HttpContext doesn't exist in a circuit.
/// </summary>
internal sealed class FanAccessToken(
    AuthenticationStateProvider authentication,
    FanTokenStore store,
    FanTokenRefresher refresher,
    TimeProvider time)
{
    /// <returns>The access token, or null for a fan who isn't signed in (or whose session has ended).</returns>
    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        var fan = (await authentication.GetAuthenticationStateAsync()).User;
        if (fan.FindFirstValue(FanSessions.SessionClaim) is not { } sessionId)
        {
            return null;
        }

        var tokens = await store.GetAsync(sessionId, cancellationToken);
        if (tokens is null)
        {
            return null;
        }

        return tokens.IsFreshAt(time.GetUtcNow())
            ? tokens.AccessToken
            : (await refresher.RefreshAsync(sessionId, cancellationToken))?.AccessToken;
    }
}
