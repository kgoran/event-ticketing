using EventTicketing.Shared.Contracts;

namespace EventTicketing.Orders.Contracts;

/// <summary>An order is paid and its seats are sold to it. Ticketing issues tickets from this (M1b).</summary>
/// <param name="CustomerEmail">
/// The buyer's email, and <paramref name="EventName"/> the event's, so consumers never call back
/// (ADR-0007). Added to version 1 in M1b: optional, because messages written before then lack them.
/// </param>
[IntegrationEvent("orders.order-confirmed", 1)]
public sealed record OrderConfirmed(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    string CustomerId,
    Guid EventId,
    long TotalMinorUnits,
    string Currency,
    IReadOnlyList<ConfirmedSeat> Seats,
    string? CustomerEmail = null,
    string? EventName = null) : IIntegrationEvent;

public sealed record ConfirmedSeat(Guid SeatId, Guid PriceCategoryId, long PriceMinorUnits);
