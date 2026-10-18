using EventTicketing.Shared.Contracts;

namespace EventTicketing.Catalog.Contracts;

/// <summary>
/// An organizer published an event: it now exists for sale. Carries every place with its price
/// category, so Inventory can create its rows and Orders its price list without calling Catalog
/// (ADR-0007).
/// </summary>
/// <param name="TimeZone">The venue's IANA time zone, for showing local times.</param>
/// <param name="OnSaleAt">When sales start; null until the organizer schedules it (OnSaleScheduled then follows).</param>
[IntegrationEvent("catalog.event-published", 1)]
public sealed record EventPublished(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid EventId,
    Guid OrganizerId,
    string Name,
    DateTimeOffset StartsAt,
    string TimeZone,
    string Currency,
    DateTimeOffset? OnSaleAt,
    IReadOnlyList<PublishedPriceCategory> PriceCategories,
    IReadOnlyList<PublishedSeat> Seats,
    IReadOnlyList<PublishedStandingArea> StandingAreas) : IIntegrationEvent;

public sealed record PublishedPriceCategory(Guid PriceCategoryId, string Name, long PriceMinorUnits);

/// <param name="SeatId">The venue seat's ID, the same in every event at the venue.</param>
/// <param name="Rank">Order for "best available" within the section: 1 is best.</param>
public sealed record PublishedSeat(Guid SeatId, string Section, string Row, int Number, int Rank, Guid PriceCategoryId);

/// <summary>A standing area sold by capacity; Inventory creates one anonymous slot per place (ADR-0011).</summary>
public sealed record PublishedStandingArea(string Section, int Capacity, Guid PriceCategoryId);
