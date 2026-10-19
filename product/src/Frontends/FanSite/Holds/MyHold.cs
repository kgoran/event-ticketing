using EventTicketing.FanSite.Seating;

namespace EventTicketing.FanSite.Holds;

/// <summary>One held place as the fan saw it when they held it: "Tribune A · Row 3 · Seat 12", 90.00.</summary>
public sealed record HeldPlace(Guid PlaceId, string Label, long PriceMinorUnits);

/// <summary>The fan's hold on one event, and its order once they check out.</summary>
public sealed record MyHold(
    Guid HoldId,
    Guid EventId,
    IReadOnlyList<HeldPlace> Places,
    DateTimeOffset ExpiresAt,
    Guid? OrderId = null,
    string? PaymentUrl = null)
{
    public long TotalMinorUnits => Places.Sum(place => place.PriceMinorUnits);

    /// <summary>Checked out: the order now decides what happens to the seats, until its own deadline.</summary>
    public bool InCheckout => OrderId is not null;

    public bool Holds(Guid placeId) => Places.Any(place => place.PlaceId == placeId);

    public TimeSpan RemainingAt(DateTimeOffset now) => ExpiresAt > now ? ExpiresAt - now : TimeSpan.Zero;

    /// <summary>Expired without a checkout: the places are free again (Inventory needs no sweep for that, ADR-0041).</summary>
    public bool HasLapsedAt(DateTimeOffset now) => !InCheckout && now >= ExpiresAt;

    /// <summary>
    /// The places PublicApi held, labelled from the seat map and in its order (row by row). Holding
    /// the best places of a standing area returns places that aren't on the map; they are labelled
    /// with the area and listed last.
    /// </summary>
    public static IReadOnlyList<HeldPlace> PlacesFrom(IEnumerable<Guid> placeIds, SeatMapModel map, string? standingSection)
    {
        var standing = map.Section(standingSection) is { IsStanding: true } section ? section : null;

        return
        [
            .. placeIds
                .Select(placeId => (Seat: map.Seat(placeId), PlaceId: placeId))
                .OrderBy(place => place.Seat is null)
                .ThenBy(place => place.Seat?.Y)
                .ThenBy(place => place.Seat?.X)
                .Select(place => place.Seat is { } seat
                    ? new HeldPlace(place.PlaceId, seat.Label, seat.Category.PriceMinorUnits)
                    : new HeldPlace(place.PlaceId, standing is null ? "Standing place" : $"{standing.Name} · standing", standing?.Category?.PriceMinorUnits ?? 0)),
        ];
    }
}
