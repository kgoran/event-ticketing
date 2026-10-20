using System.Buffers.Text;
using System.Security.Cryptography;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Ticketing.Domain;
using EventTicketing.Ticketing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Ticketing.Infrastructure.Endpoints;

/// <summary>One of the fan's tickets, with what a ticket shows: the event, the place, and the code for the QR.</summary>
/// <param name="TimeZone">The venue's IANA time zone, for showing <paramref name="StartsAt"/> locally.</param>
/// <param name="Code">The QR code's content: a JWS signed with the event's key (ADR-0051).</param>
public sealed record MyTicket(
    Guid TicketId,
    Guid OrderId,
    Guid EventId,
    string EventName,
    DateTimeOffset StartsAt,
    string TimeZone,
    string Label,
    string Code,
    DateTimeOffset IssuedAt);

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
        // "My tickets" reads Ticketing directly, so a fan has their tickets even if the email never
        // arrives (overview §4.4). Only the caller's own tickets; nothing is cached on the way.
        endpoints.MapGet("/tickets", async (Guid? orderId, TicketingDbContext db, IExecutionContext caller, HttpResponse response, CancellationToken cancellationToken) =>
            {
                var tickets = await (
                        from ticket in db.Tickets.AsNoTracking()
                        join ticketedEvent in db.Events.AsNoTracking() on ticket.EventId equals ticketedEvent.EventId
                        where ticket.CustomerId == caller.UserId && (orderId == null || ticket.OrderId == orderId)
                        orderby ticketedEvent.StartsAt, ticket.OrderId, ticket.Label
                        select new MyTicket(
                            ticket.Id,
                            ticket.OrderId,
                            ticket.EventId,
                            ticketedEvent.Name,
                            ticketedEvent.StartsAt,
                            ticketedEvent.TimeZone,
                            ticket.Label,
                            ticket.Code,
                            ticket.IssuedAt))
                    .ToListAsync(cancellationToken);

                response.Headers.CacheControl = "no-store";
                return Results.Ok(tickets);
            })
            .WithTags("Tickets")
            .WithName("GetMyTickets")
            .WithSummary("Your tickets, soonest event first; with orderId, one order's (empty until they are issued, seconds after paying)")
            .Produces<IReadOnlyList<MyTicket>>();

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
