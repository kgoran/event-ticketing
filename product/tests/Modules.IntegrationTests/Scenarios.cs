using System.Net;
using System.Net.Http.Json;
using EventTicketing.Catalog.Browse;
using EventTicketing.Catalog.Venues;
using EventTicketing.Inventory.Application.Holds;
using EventTicketing.Inventory.Infrastructure.Endpoints;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>An event on sale, and its seats in seat-map order (row by row, left to right).</summary>
internal sealed record Sale(Guid OrganizerId, Guid EventId, IReadOnlyList<SeatMapSeat> SeatList)
{
    public IReadOnlyList<Guid> Seats { get; } = [.. SeatList.Select(s => s.SeatId)];

    public IEnumerable<Guid> SeatIdsAt(params (string Row, int Number)[] places) =>
        places.Select(place => SeatList.Single(s => s.Row == place.Row && s.Number == place.Number).SeatId);
}

/// <summary>The steps most scenarios start with, through the real APIs.</summary>
internal static class Scenarios
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>A new organizer's 10-seat event (rows 1–2 of 5 seats), published and on sale, with Inventory's rows created.</summary>
    public static async Task<Sale> OnSaleAsync(this TestSystem system, DateTimeOffset? onSaleAt = null, bool withFloor = false, long priceMinorUnits = 2500)
    {
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));

        var venue = Venues.Small(organizerId, rows: 2, seatsPerRow: 5);
        if (withFloor)
        {
            venue = venue with { Sections = [.. venue.Sections, new VenueSectionInput("FLOOR", "Floor", SectionKind.Standing, Capacity: 20)] };
        }

        var venueId = await backOffice.ImportVenueAsync(venue);
        var eventId = await backOffice.CreateEventAsync(
            BackOffice.SingleCategoryEvent(organizerId, venueId, withFloor ? ["A", "FLOOR"] : ["A"], priceMinorUnits));
        (await backOffice.ScheduleOnSaleAsync(eventId, onSaleAt ?? DateTimeOffset.UtcNow)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await backOffice.PublishAsync(eventId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await system.DeliverMessagesAsync();

        using var anonymous = system.PublicApi();
        var page = (await anonymous.GetFromJsonAsync<EventPage>(new Uri($"/events/{eventId}", UriKind.Relative), CancellationToken))!;
        var map = (await anonymous.GetFromJsonAsync<SeatMap>(new Uri(page.SeatMapUrl, UriKind.Relative), CancellationToken))!;
        return new Sale(organizerId, eventId, [.. map.Sections.SelectMany(s => s.Seats)]);
    }

    public static Task<HttpResponseMessage> HoldAsync(this HttpClient fan, Guid eventId, params Guid[] seatIds) =>
        fan.PostAsJsonAsync(new Uri($"/events/{eventId}/holds", UriKind.Relative), new PlaceHoldRequest(SeatIds: seatIds), CancellationToken);

    public static Task<HttpResponseMessage> HoldBestAsync(this HttpClient fan, Guid eventId, string section, int quantity) =>
        fan.PostAsJsonAsync(new Uri($"/events/{eventId}/holds", UriKind.Relative), new PlaceHoldRequest(Section: section, Quantity: quantity), CancellationToken);

    public static async Task<HoldPlaced> PlacedAsync(this HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.Content.ReadFromJsonAsync<HoldPlaced>(CancellationToken))!;
    }

    public static async Task<Availability> AvailabilityAsync(this TestSystem system, Guid eventId)
    {
        using var anonymous = system.PublicApi();
        return (await anonymous.GetFromJsonAsync<Availability>(new Uri($"/events/{eventId}/availability", UriKind.Relative), CancellationToken))!;
    }

    /// <summary>Moves a hold's expiry into the past, as if its lifetime (or backstop) had run out.</summary>
    public static Task ExpireHoldAsync(this TestSystem system, Guid holdId) =>
        system.QueryAsync<int>(
            "WITH s AS (UPDATE inventory.seats SET held_until = now() - interval '1 second' WHERE hold_id = @p0 RETURNING 1), " +
            "h AS (UPDATE inventory.holds SET expires_at = now() - interval '1 second' WHERE id = @p0 RETURNING 1) " +
            "SELECT count(*)::int FROM s",
            holdId);

    /// <summary>Makes the recurring sweeper due and runs it (the first delivery in a test run starts it).</summary>
    public static async Task SweepNowAsync(this TestSystem system)
    {
        await system.DeliverMessagesAsync();
        await system.QueryAsync<int>(
            "WITH due AS (UPDATE inventory.scheduled_messages SET due_at = now() - interval '1 second' " +
            "WHERE type = @p0 AND dead_lettered_at IS NULL RETURNING 1) SELECT count(*)::int FROM due",
            typeof(SweepExpiredHolds).FullName!);
        await system.DeliverMessagesAsync();
    }
}
