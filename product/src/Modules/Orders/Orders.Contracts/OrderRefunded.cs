using EventTicketing.Shared.Contracts;

namespace EventTicketing.Orders.Contracts;

/// <summary>A paid order's money went back to the fan, e.g. because its seats were lost (Notifications tells the fan, M1b).</summary>
/// <param name="Reason">SeatsLost or PaidAfterClose.</param>
/// <param name="CustomerEmail">Added to version 1 in M1b, with <paramref name="EventName"/>; optional (ADR-0007).</param>
[IntegrationEvent("orders.order-refunded", 1)]
public sealed record OrderRefunded(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    string CustomerId,
    Guid EventId,
    long AmountMinorUnits,
    string Currency,
    string Reason,
    string? CustomerEmail = null,
    string? EventName = null) : IIntegrationEvent;
