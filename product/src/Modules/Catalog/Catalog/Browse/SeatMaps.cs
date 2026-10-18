using System.Security.Cryptography;
using System.Text.Json;
using EventTicketing.Catalog.Events;
using EventTicketing.Catalog.Venues;

namespace EventTicketing.Catalog.Browse;

/// <summary>Builds an event's seat map, and the content hash that versions its URL.</summary>
internal static class SeatMaps
{
    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);

    public static SeatMap Of(Event @event, Venue venue) => Build(@event, venue, @event.SeatMapVersion ?? string.Empty);

    /// <summary>
    /// The first 12 hex digits of a SHA-256 over the map's content: the same layout always gives the
    /// same URL, and any change gives a new one, so caches never need purging (ADR-0028).
    /// </summary>
    public static string VersionOf(Event @event, Venue venue)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(Build(@event, venue, string.Empty), Canonical);
        return Convert.ToHexStringLower(SHA256.HashData(content))[..12];
    }

    private static SeatMap Build(Event @event, Venue venue, string version) => new(
        @event.Id,
        version,
        [.. @event.PriceCategories.OrderBy(c => c.Id).Select(c => new SeatMapCategory(c.Id, c.Name))],
        [.. venue.Sections.OrderBy(s => s.Position).Select(section => new SeatMapSection(
            section.Code,
            section.Name,
            section.Kind.ToString(),
            section.X,
            section.Y,
            section.Capacity,
            section.Kind == SectionKind.Standing ? @event.PriceCategoryIdFor(section.Code, null) : null,
            [.. section.Seats
                .OrderBy(seat => seat.Y).ThenBy(seat => seat.X)
                .Select(seat => new SeatMapSeat(seat.Id, seat.Row, seat.Number, seat.X, seat.Y, @event.PriceCategoryIdFor(section.Code, seat.Row)!.Value))]))]);
}
