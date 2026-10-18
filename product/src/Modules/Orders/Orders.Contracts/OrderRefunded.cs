using EventTicketing.Shared.Contracts;

namespace EventTicketing.Orders.Contracts;

/// <summary>A paid order's money went back to the fan, e.g. because its seats were lost (Notifications tells the fan, M1b).</summary>
/// <param name="Reason">SeatsLost or PaidAfterClose.</param>
[IntegrationEvent("orders.order-refunded", 1)]
public sealed record OrderRefunded(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    string CustomerId,
    Guid EventId,
    long AmountMinorUnits,
    string Currency,
    string Reason) : IIntegrationEvent;
