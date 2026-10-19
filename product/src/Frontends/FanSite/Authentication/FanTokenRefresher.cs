using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EventTicketing.FanSite.Authentication;

/// <summary>
/// Swaps a fan's refresh token for new tokens at the identity provider's token endpoint. A circuit
/// can make several API calls at once, so one refresh per session runs at a time and the others
/// use its result.
/// </summary>
internal sealed partial class FanTokenRefresher(
    IOptionsMonitor<OpenIdConnectOptions> openIdConnect,
    FanTokenStore store,
    TimeProvider time,
    ILogger<FanTokenRefresher> logger)
{
    // A fixed set of locks shared by hash, so nothing has to clean up per-session locks.
    private readonly SemaphoreSlim[] _locks = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    /// <returns>Fresh tokens, or null when the fan's session has ended and they must sign in again.</returns>
    public async Task<FanTokens?> RefreshAsync(string sessionId, CancellationToken cancellationToken)
    {
        var gate = _locks[(uint)StringComparer.Ordinal.GetHashCode(sessionId) % _locks.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var tokens = await store.GetAsync(sessionId, cancellationToken);
            if (tokens is null || tokens.IsFreshAt(time.GetUtcNow()))
            {
                return tokens; // signed out, or refreshed by another call while this one waited
            }

            if (tokens.RefreshToken is null)
            {
                await store.RemoveAsync(sessionId, cancellationToken);
                return null;
            }

            var options = openIdConnect.Get(OpenIdConnectDefaults.AuthenticationScheme);
            var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            using var request = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ClientSecret!,
                ["refresh_token"] = tokens.RefreshToken,
            });

            using var response = await options.Backchannel.PostAsync(new Uri(configuration.TokenEndpoint), request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogRefreshFailed(logger, (int)response.StatusCode);

                // 400 or 401: the refresh token is no longer valid (the session ended at the identity
                // provider). Anything else may pass, so the session stays and the next call tries again.
                if ((int)response.StatusCode is 400 or 401)
                {
                    await store.RemoveAsync(sessionId, cancellationToken);
                }

                return null;
            }

            var message = new OpenIdConnectMessage(await response.Content.ReadAsStringAsync(cancellationToken));
            var refreshed = FanTokens.From(message, time.GetUtcNow(), previous: tokens);
            await store.SaveAsync(sessionId, refreshed, FanTokens.SessionLifetime(message), cancellationToken);
            return refreshed;
        }
        finally
        {
            gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing a fan's tokens failed with status {StatusCode}")]
    private static partial void LogRefreshFailed(ILogger logger, int statusCode);
}
