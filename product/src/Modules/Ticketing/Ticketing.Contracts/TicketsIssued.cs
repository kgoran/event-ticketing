using EventTicketing.Shared.Contracts;

namespace EventTicketing.Ticketing.Contracts;

/// <summary>
/// A confirmed order's tickets exist: one per seat, each with its signed code (ADR-0051).
/// Notifications emails them from this alone; CheckIn adds them to the door list (M3).
/// </summary>
/// <param name="CustomerEmail">The buyer's email, when their token had one (ADR-0007).</param>
/// <param name="TimeZone">The venue's IANA time zone, for showing <paramref name="StartsAt"/> locally.</param>
[IntegrationEvent("ticketing.tickets-issued", 1)]
public sealed record TicketsIssued(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    string CustomerId,
    string? CustomerEmail,
    Guid EventId,
    string EventName,
    DateTimeOffset StartsAt,
    string TimeZone,
    IReadOnlyList<IssuedTicket> Tickets) : IIntegrationEvent;

/// <param name="Label">Where the ticket gets you, e.g. "A · Row 3 · Seat 12" or "FLOOR · standing".</param>
/// <param name="Code">The QR code's content: a JWS signed with the event's key. A bearer code: whoever shows it gets in.</param>
public sealed record IssuedTicket(Guid TicketId, Guid SeatId, string Label, string Code);
