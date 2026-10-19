using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace EventTicketing.FanSite.Authentication;

/// <summary>
/// Signed-in fans' tokens, by the session key in their cookie. Encrypted with Data Protection and
/// kept in <see cref="IDistributedCache"/>: in memory while the fan site runs as one instance, and
/// in Valkey once it runs several, without changing this class (ADR-0049).
/// </summary>
internal sealed class FanTokenStore(IDistributedCache cache, IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("EventTicketing.FanSite.Tokens");

    public async Task SaveAsync(string sessionId, FanTokens tokens, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var protectedTokens = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens));
        await cache.SetAsync(Key(sessionId), protectedTokens, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime }, cancellationToken);
    }

    public async Task<FanTokens?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        var protectedTokens = await cache.GetAsync(Key(sessionId), cancellationToken);
        if (protectedTokens is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FanTokens>(_protector.Unprotect(protectedTokens));
        }
        catch (CryptographicException)
        {
            // Encrypted with a key this instance doesn't have: as good as signed out.
            return null;
        }
    }

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken) =>
        cache.RemoveAsync(Key(sessionId), cancellationToken);

    private static string Key(string sessionId) => "fansite:tokens:" + sessionId;
}
