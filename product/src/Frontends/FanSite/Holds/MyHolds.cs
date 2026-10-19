using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace EventTicketing.FanSite.Holds;

/// <summary>
/// The fan's current hold per event, remembered in this browser (encrypted with Data Protection):
/// it has to survive a reload and the round trip to the payment page, and PublicApi has no
/// "my current hold" endpoint yet. A hold made in another browser stays invisible here; the API
/// then answers "you already hold seats" until it expires. Browser storage needs JavaScript, so
/// this works only once the circuit is interactive (from OnAfterRenderAsync).
/// </summary>
internal sealed class MyHolds(ProtectedLocalStorage storage)
{
    public async Task<MyHold?> GetAsync(string fanId, Guid eventId)
    {
        try
        {
            var stored = await storage.GetAsync<MyHold>(Key(fanId, eventId));
            return stored.Success ? stored.Value : null;
        }
        catch (CryptographicException)
        {
            // Stored under a key this site no longer has (e.g. a new container): forget it.
            await storage.DeleteAsync(Key(fanId, eventId));
            return null;
        }
    }

    public ValueTask SaveAsync(string fanId, MyHold hold) => storage.SetAsync(Key(fanId, hold.EventId), hold);

    public ValueTask ForgetAsync(string fanId, Guid eventId) => storage.DeleteAsync(Key(fanId, eventId));

    private static string Key(string fanId, Guid eventId) => $"hold:{fanId}:{eventId}";
}
