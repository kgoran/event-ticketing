using System.Net;
using System.Net.Http.Json;
using EventTicketing.Catalog.Browse;
using EventTicketing.Catalog.Events;
using EventTicketing.Catalog.Venues;
using EventTicketing.Inventory.Application.Holds;
using EventTicketing.Inventory.Infrastructure.Endpoints;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>M1a I1: places created from EventPublished, all-or-nothing claims with expiry inside them, hold limits, best available, availability.</summary>
public sealed class InventoryTests(TestSystem system)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Claims_are_all_or_nothing()
    {
        var sale = await OnSaleAsync();
        var (s1, s2, s3) = (sale.Seats[0], sale.Seats[1], sale.Seats[2]);
        using var ana = system.PublicApi(Callers.Fan());
        using var ben = system.PublicApi(Callers.Fan());

        (await HoldAsync(ana, sale.EventId, s1, s2)).StatusCode.Should().Be(HttpStatusCode.Created);

        var overlapping = await HoldAsync(ben, sale.EventId, s2, s3);
        overlapping.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await overlapping.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("hold.seats_unavailable");

        // Ben's failed attempt took nothing: s3 is still free, and he can have it alone.
        (await AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(s3).And.NotContain([s1, s2]);
        (await HoldAsync(ben, sale.EventId, s3)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task An_expired_seat_can_be_claimed_immediately()
    {
        var sale = await OnSaleAsync();
        var seat = sale.Seats[0];
        using var ana = system.PublicApi(Callers.Fan());
        using var ben = system.PublicApi(Callers.Fan());

        var anasHold = await PlacedAsync(await HoldAsync(ana, sale.EventId, seat));
        (await HoldAsync(ben, sale.EventId, seat)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Nothing sweeps expired holds: the claim itself treats an expired seat as free (ADR-0011).
        await ExpireAsync(anasHold.HoldId);
        (await AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(seat);
        (await HoldAsync(ben, sale.EventId, seat)).StatusCode.Should().Be(HttpStatusCode.Created);

        // Ana's lapsed hold doesn't count against her limit either.
        (await HoldAsync(ana, sale.EventId, sale.Seats[1])).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_fan_has_one_active_hold_per_event_until_releasing_it()
    {
        var sale = await OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());

        var first = await PlacedAsync(await HoldAsync(ana, sale.EventId, sale.Seats[0]));

        var second = await HoldAsync(ana, sale.EventId, sale.Seats[1]);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("hold.active_hold_exists");

        // Only the fan who holds it can release it; anyone else is told it doesn't exist.
        using var ben = system.PublicApi(Callers.Fan());
        (await ben.DeleteAsync(new Uri($"/holds/{first.HoldId}", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await ana.DeleteAsync(new Uri($"/holds/{first.HoldId}", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(sale.Seats[0]);
        (await HoldAsync(ana, sale.EventId, sale.Seats[1])).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Two_simultaneous_holds_by_one_fan_leave_one_hold_and_no_stray_seats()
    {
        var sale = await OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan("ana-" + Guid.NewGuid().ToString("N")));

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => HoldAsync(ana, sale.EventId, sale.Seats[i])));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict);

        // The losers' claims were rolled back with their transactions.
        (await AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().HaveCount(sale.Seats.Count - 1);
    }

    [Fact]
    public async Task A_hold_has_at_most_eight_seats()
    {
        var sale = await OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());

        (await HoldAsync(ana, sale.EventId, [.. sale.Seats.Take(9)])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await HoldBestAsync(ana, sale.EventId, "A", 9)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Best_available_takes_the_best_ranked_free_places_of_a_section()
    {
        var sale = await OnSaleAsync(withFloor: true);
        using var ana = system.PublicApi(Callers.Fan());
        using var ben = system.PublicApi(Callers.Fan());
        using var cid = system.PublicApi(Callers.Fan());

        // Row 1 of 5 seats: the middle seat first, then its neighbours.
        var best = await PlacedAsync(await HoldBestAsync(ana, sale.EventId, "A", 3));
        best.SeatIds.Should().BeEquivalentTo(sale.SeatIdsAt(("1", 3), ("1", 2), ("1", 4)));

        var next = await PlacedAsync(await HoldBestAsync(ben, sale.EventId, "A", 2));
        next.SeatIds.Should().BeEquivalentTo(sale.SeatIdsAt(("1", 1), ("1", 5)));

        // Standing places are slots, held the same way.
        var floor = await PlacedAsync(await HoldBestAsync(cid, sale.EventId, "FLOOR", 4));
        floor.SeatIds.Should().HaveCount(4);

        var availability = await AvailabilityAsync(sale.EventId);
        availability.Sections.Should().ContainEquivalentOf(new SectionAvailability("A", 5, 10));
        availability.Sections.Should().ContainEquivalentOf(new SectionAvailability("FLOOR", 16, 20));
        availability.AvailableSeatIds.Should().HaveCount(5, "standing slots are counted, not listed");

        using var dan = system.PublicApi(Callers.Fan());
        var tooMany = await HoldBestAsync(dan, sale.EventId, "A", 6);
        tooMany.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AvailabilityAsync(sale.EventId)).Sections.Should().ContainEquivalentOf(new SectionAvailability("A", 5, 10));
    }

    [Fact]
    public async Task Nothing_can_be_held_before_sales_start()
    {
        var sale = await OnSaleAsync(onSaleAt: DateTimeOffset.UtcNow.AddDays(1));
        using var ana = system.PublicApi(Callers.Fan());

        var early = await HoldAsync(ana, sale.EventId, sale.Seats[0]);
        early.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await early.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("hold.not_on_sale");
        (await AvailabilityAsync(sale.EventId)).OnSale.Should().BeFalse();

        // The organizer opens sales now; OnSaleScheduled reaches Inventory.
        using var backOffice = system.BackOfficeApi(Callers.Organizer(sale.OrganizerId));
        (await backOffice.ScheduleOnSaleAsync(sale.EventId, DateTimeOffset.UtcNow)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await system.DeliverMessagesAsync();

        (await AvailabilityAsync(sale.EventId)).OnSale.Should().BeTrue();
        (await HoldAsync(ana, sale.EventId, sale.Seats[0])).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Seats_of_another_event_cannot_be_held()
    {
        var first = await OnSaleAsync();
        var second = await OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());

        (await HoldAsync(ana, first.EventId, second.Seats[0])).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private async Task<Sale> OnSaleAsync(DateTimeOffset? onSaleAt = null, bool withFloor = false)
    {
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));

        var venue = Venues.Small(organizerId, rows: 2, seatsPerRow: 5);
        if (withFloor)
        {
            venue = venue with { Sections = [.. venue.Sections, new VenueSectionInput("FLOOR", "Floor", SectionKind.Standing, Capacity: 20)] };
        }

        var venueId = await backOffice.ImportVenueAsync(venue);
        var eventId = await backOffice.CreateEventAsync(BackOffice.SingleCategoryEvent(organizerId, venueId, withFloor ? ["A", "FLOOR"] : ["A"]));
        (await backOffice.ScheduleOnSaleAsync(eventId, onSaleAt ?? DateTimeOffset.UtcNow)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await backOffice.PublishAsync(eventId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await system.DeliverMessagesAsync();

        using var anonymous = system.PublicApi();
        var page = (await anonymous.GetFromJsonAsync<EventPage>(new Uri($"/events/{eventId}", UriKind.Relative), CancellationToken))!;
        var map = (await anonymous.GetFromJsonAsync<SeatMap>(new Uri(page.SeatMapUrl, UriKind.Relative), CancellationToken))!;
        return new Sale(organizerId, eventId, [.. map.Sections.SelectMany(s => s.Seats)]);
    }

    private static Task<HttpResponseMessage> HoldAsync(HttpClient fan, Guid eventId, params Guid[] seatIds) =>
        fan.PostAsJsonAsync(new Uri($"/events/{eventId}/holds", UriKind.Relative), new PlaceHoldRequest(SeatIds: seatIds), CancellationToken);

    private static Task<HttpResponseMessage> HoldBestAsync(HttpClient fan, Guid eventId, string section, int quantity) =>
        fan.PostAsJsonAsync(new Uri($"/events/{eventId}/holds", UriKind.Relative), new PlaceHoldRequest(Section: section, Quantity: quantity), CancellationToken);

    private static async Task<HoldPlaced> PlacedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.Content.ReadFromJsonAsync<HoldPlaced>(CancellationToken))!;
    }

    private async Task<Availability> AvailabilityAsync(Guid eventId)
    {
        using var anonymous = system.PublicApi();
        return (await anonymous.GetFromJsonAsync<Availability>(new Uri($"/events/{eventId}/availability", UriKind.Relative), CancellationToken))!;
    }

    /// <summary>Moves a hold's expiry into the past, as if its lifetime had run out.</summary>
    private async Task ExpireAsync(Guid holdId)
    {
        await system.QueryAsync<int>(
            "WITH s AS (UPDATE inventory.seats SET held_until = now() - interval '1 second' WHERE hold_id = @p0 RETURNING 1), " +
            "h AS (UPDATE inventory.holds SET expires_at = now() - interval '1 second' WHERE id = @p0 RETURNING 1) " +
            "SELECT count(*)::int FROM s",
            holdId);
    }

    private sealed record Sale(Guid OrganizerId, Guid EventId, IReadOnlyList<SeatMapSeat> SeatList)
    {
        public IReadOnlyList<Guid> Seats { get; } = [.. SeatList.Select(s => s.SeatId)];

        public IEnumerable<Guid> SeatIdsAt(params (string Row, int Number)[] places) =>
            places.Select(place => SeatList.Single(s => s.Row == place.Row && s.Number == place.Number).SeatId);
    }
}
