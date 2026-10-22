using System.Buffers.Text;
using System.Security.Cryptography;
using EventTicketing.Ticketing.Contracts;
using EventTicketing.Ticketing.Domain;
using EventTicketing.Ticketing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Ticketing.Infrastructure;

/// <summary>Ticketing's module API: the events' public keys, as a JSON Web Key Set.</summary>
internal sealed class TicketingApi(TicketingDbContext db) : ITicketingApi
{
    public async Task<TicketKeySet?> KeysAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var keys = await db.EventKeys.AsNoTracking()
            .Where(key => key.EventId == eventId)
            .OrderBy(key => key.CreatedAt)
            .ToListAsync(cancellationToken);

        return keys.Count == 0 ? null : new TicketKeySet([.. keys.Select(Jwk)]);
    }

    private static TicketKey Jwk(EventKey key)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key.PublicKey, out _);
        var point = ecdsa.ExportParameters(includePrivateParameters: false).Q;
        return new TicketKey("EC", "P-256", Base64Url.EncodeToString(point.X), Base64Url.EncodeToString(point.Y), key.KeyId, "ES256", "sig");
    }
}
