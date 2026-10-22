using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Http;
using EventTicketing.Ticketing.Application.Revocation;
using EventTicketing.Ticketing.Contracts;
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

/// <summary>Why an admin revokes a ticket (D63), for the record and for TicketsRevoked.</summary>
public sealed record RevokeTicketRequest(string Reason);

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
        // gets them with the door list (CheckIn); this is the same set in the standard format.
        endpoints.MapGet("/events/{eventId:guid}/ticket-keys", async (Guid eventId, ITicketingApi ticketing, HttpResponse response, CancellationToken cancellationToken) =>
            {
                if (await ticketing.KeysAsync(eventId, cancellationToken) is not { } keys)
                {
                    return Results.NotFound();
                }

                response.Headers.CacheControl = "public, max-age=300";
                return Results.Ok(keys);
            })
            .AllowAnonymous()
            .WithTags("Tickets")
            .WithName("GetTicketKeys")
            .WithSummary("The public keys that verify an event's ticket codes, as a JSON Web Key Set")
            .Produces<TicketKeySet>()
            .Produces(StatusCodes.Status404NotFound);
    }

    public static void MapForAdmins(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/tickets/{ticketId:guid}/revocation", async (Guid ticketId, RevokeTicketRequest request, ICommandHandler<RevokeTicket, Unit> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new RevokeTicket(ticketId, request.Reason), cancellationToken);
                return result.IsSuccess ? Results.NoContent() : result.Error.ToProblem();
            })
            .WithTags("Tickets")
            .WithName("RevokeTicket")
            .WithSummary("Revokes a ticket, so the door refuses it once the devices have synced (admins only)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
