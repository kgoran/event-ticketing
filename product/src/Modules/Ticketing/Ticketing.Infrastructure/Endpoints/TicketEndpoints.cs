using System.Buffers.Text;
using System.Security.Cryptography;
using EventTicketing.Ticketing.Domain;
using EventTicketing.Ticketing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Ticketing.Infrastructure.Endpoints;

/// <summary>A JSON Web Key Set (RFC 7517): the public keys that verify an event's ticket codes.</summary>
public sealed record TicketKeySet(IReadOnlyList<TicketKey> Keys);

/// <summary>An EC public key as a JWK: P-256, for ES256 signatures, named by <paramref name="Kid"/>.</summary>
public sealed record TicketKey(string Kty, string Crv, string X, string Y, string Kid, string Alg, string Use)
{
    internal static TicketKey From(EventKey key)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key.PublicKey, out _);
        var point = ecdsa.ExportParameters(includePrivateParameters: false).Q;
        return new TicketKey("EC", "P-256", Base64Url.EncodeToString(point.X), Base64Url.EncodeToString(point.Y), key.KeyId, "ES256", "sig");
    }
}

internal static class TicketEndpoints
{
    public static void MapForFans(IEndpointRouteBuilder endpoints)
    {
        // Public keys only: anyone may verify a ticket, nobody can make one (ADR-0051). The scanner
        // gets them with the door list (M3); this is the same set in the standard format.
        endpoints.MapGet("/events/{eventId:guid}/ticket-keys", async (Guid eventId, TicketingDbContext db, HttpResponse response, CancellationToken cancellationToken) =>
            {
                var keys = await db.EventKeys.AsNoTracking()
                    .Where(key => key.EventId == eventId)
                    .OrderBy(key => key.CreatedAt)
                    .ToListAsync(cancellationToken);

                if (keys.Count == 0)
                {
                    return Results.NotFound();
                }

                response.Headers.CacheControl = "public, max-age=300";
                return Results.Ok(new TicketKeySet([.. keys.Select(TicketKey.From)]));
            })
            .AllowAnonymous()
            .WithTags("Tickets")
            .WithName("GetTicketKeys")
            .WithSummary("The public keys that verify an event's ticket codes, as a JSON Web Key Set")
            .Produces<TicketKeySet>()
            .Produces(StatusCodes.Status404NotFound);
    }
}
