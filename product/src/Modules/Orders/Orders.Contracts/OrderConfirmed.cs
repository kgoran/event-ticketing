using EventTicketing.Shared.Contracts;

namespace EventTicketing.Orders.Contracts;

/// <summary>An order is paid and its seats are sold to it. Ticketing issues tickets from this (M1b).</summary>
[IntegrationEvent("orders.order-confirmed", 1)]
public sealed record OrderConfirmed(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    string CustomerId,
    Guid EventId,
    long TotalMinorUnits,
    string Currency,
    IReadOnlyList<ConfirmedSeat> Seats) : IIntegrationEvent;

public sealed record ConfirmedSeat(Guid SeatId, Guid PriceCategoryId, long PriceMinorUnits);
