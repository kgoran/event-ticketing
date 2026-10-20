using System.Security.Cryptography;
using EventTicketing.Ticketing.Application;
using EventTicketing.Ticketing.Domain;
using EventTicketing.Ticketing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Ticketing.Infrastructure.Signing;

/// <summary>Creates and finds the events' signing keys. The private key leaves this class only encrypted.</summary>
internal sealed class EventKeys(TicketingDbContext db, MasterKey masterKey, TimeProvider time) : IEventKeys
{
    public async Task EnsureForAsync(Guid eventId, CancellationToken cancellationToken)
    {
        if (await CurrentAsync(eventId, cancellationToken) is not null)
        {
            return;
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyId = Guid.CreateVersion7().ToString("N");
        var privateKey = ecdsa.ExportPkcs8PrivateKey();
        try
        {
            db.EventKeys.Add(new EventKey(eventId, keyId, ecdsa.ExportSubjectPublicKeyInfo(), masterKey.Encrypt(privateKey, keyId), time.GetUtcNow()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    /// <summary>The newest key: when keys rotate, new tickets get the new one and old ones still verify.</summary>
    public async Task<EventKey?> CurrentAsync(Guid eventId, CancellationToken cancellationToken) =>
        db.EventKeys.Local.Where(key => key.EventId == eventId).MaxBy(key => key.CreatedAt)
        ?? await db.EventKeys.AsNoTracking()
            .Where(key => key.EventId == eventId)
            .OrderByDescending(key => key.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
}
