using EventTicketing.Shared.Contracts;

namespace EventTicketing.Catalog.Contracts;

/// <summary>A published event's prices changed. Carries the full price list, not only the changes.</summary>
[IntegrationEvent("catalog.prices-changed", 1)]
public sealed record PricesChanged(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid EventId,
    string Currency,
    IReadOnlyList<PublishedPriceCategory> PriceCategories) : IIntegrationEvent;
