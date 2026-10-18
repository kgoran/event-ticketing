using EventTicketing.Shared.Contracts;

namespace EventTicketing.Catalog.Contracts;

/// <summary>
/// An organizer published an event: it now exists for sale. Carries the seat layout and prices, so
/// Inventory can create its seat rows and Orders its price list without calling Catalog (ADR-0007).
/// </summary>
[IntegrationEvent("catalog.event-published", 1)]
public sealed record EventPublished(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid EventId,
    Guid OrganizerId,
    string Name,
    DateTimeOffset StartsAt,
    string Currency,
    IReadOnlyList<PublishedPriceCategory> PriceCategories,
    IReadOnlyList<PublishedSeat> Seats) : IIntegrationEvent;

public sealed record PublishedPriceCategory(Guid PriceCategoryId, string Name, long PriceMinorUnits);

public sealed record PublishedSeat(Guid SeatId, string Section, string Row, int Number, Guid PriceCategoryId);
