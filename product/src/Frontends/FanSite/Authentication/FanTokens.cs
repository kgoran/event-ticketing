using System.Globalization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EventTicketing.FanSite.Authentication;

/// <summary>A signed-in fan's tokens from the identity provider, kept on the server.</summary>
internal sealed record FanTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string? RefreshToken, string? IdToken)
{
    /// <summary>A token this close to expiry is refreshed first, so it can't expire on its way to PublicApi.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    /// <summary>How long the tokens are kept when the response doesn't say how long the refresh token lasts.</summary>
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromMinutes(30);

    public bool IsFreshAt(DateTimeOffset now) => AccessTokenExpiresAt - now > RefreshMargin;

    /// <summary>
    /// The tokens from a token-endpoint response. A refresh response may leave out the refresh and
    /// ID tokens; then the previous ones still apply.
    /// </summary>
    public static FanTokens From(OpenIdConnectMessage response, DateTimeOffset now, FanTokens? previous = null) => new(
        response.AccessToken ?? throw new InvalidOperationException("The token response has no access token."),
        now + Seconds(response.ExpiresIn, fallback: TimeSpan.FromMinutes(5)),
        response.RefreshToken ?? previous?.RefreshToken,
        response.IdToken ?? previous?.IdToken);

    /// <summary>How long the server keeps the tokens: as long as the refresh token lasts.</summary>
    public static TimeSpan SessionLifetime(OpenIdConnectMessage response) =>
        Seconds(response.GetParameter("refresh_expires_in"), DefaultSessionLifetime);

    private static TimeSpan Seconds(string? value, TimeSpan fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : fallback;
}
