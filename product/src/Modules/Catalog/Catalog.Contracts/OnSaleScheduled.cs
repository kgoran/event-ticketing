using EventTicketing.Shared.Contracts;

namespace EventTicketing.Catalog.Contracts;

/// <summary>A published event's sales start at <paramref name="OnSaleAt"/>: set for the first time, or moved.</summary>
[IntegrationEvent("catalog.on-sale-scheduled", 1)]
public sealed record OnSaleScheduled(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid EventId,
    DateTimeOffset OnSaleAt) : IIntegrationEvent;
