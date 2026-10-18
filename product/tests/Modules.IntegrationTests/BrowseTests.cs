using System.Net;
using System.Net.Http.Json;
using EventTicketing.Catalog.Browse;
using EventTicketing.Catalog.Events;
using EventTicketing.Inventory.Infrastructure.Endpoints;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>M1a C2: fans browse published events, search them, and load seat maps.</summary>
public sealed class BrowseTests(TestSystem system)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Search_finds_published_events_by_event_or_venue_name()
    {
        // A word no other test uses, so the shared database's other events don't match.
        var word = "zx" + Guid.NewGuid().ToString("N")[..10];
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));

        var venueId = await backOffice.ImportVenueAsync(Venues.Small(organizerId) with { Name = $"Hall {word}" });
        var jazz = await PublishedEventAsync(backOffice, organizerId, venueId, $"Jazz night {word}", daysAhead: 20);
        var rock = await PublishedEventAsync(backOffice, organizerId, venueId, "Rock evening", daysAhead: 10);
        await backOffice.CreateEventAsync(BackOffice.SingleCategoryEvent(organizerId, venueId, ["A"]) with { Name = $"Draft {word}" });

        using var fan = system.PublicApi();

        // The venue's name matches both published events; the draft is not listed. Soonest first among equals.
        var byVenue = await ListAsync(fan, $"?q={word}");
        byVenue.Items.Select(e => e.EventId).Should().BeEquivalentTo([jazz, rock]);

        // Matching the event's own name as well ranks it first.
        var byName = await ListAsync(fan, $"?q=jazz {word}");
        byName.Items.Select(e => e.EventId).Should().Equal(jazz);

        var summary = byVenue.Items.Single(e => e.EventId == jazz);
        summary.VenueName.Should().Be($"Hall {word}");
        summary.TimeZone.Should().Be("Europe/Zagreb");
        summary.PriceFromMinorUnits.Should().Be(2500);
        summary.SalesPhase.Should().Be("OnSale");
    }

    [Fact]
    public async Task The_list_is_paged_without_a_total()
    {
        var word = "zx" + Guid.NewGuid().ToString("N")[..10];
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));
        var venueId = await backOffice.ImportVenueAsync(Venues.Small(organizerId));
        for (var day = 1; day <= 3; day++)
        {
            await PublishedEventAsync(backOffice, organizerId, venueId, $"Series {word} part {day}", daysAhead: day);
        }

        using var fan = system.PublicApi();
        var first = await ListAsync(fan, $"?q={word}&pageSize=2");
        var second = await ListAsync(fan, $"?q={word}&pageSize=2&page=2");

        first.Items.Should().HaveCount(2);
        first.HasMore.Should().BeTrue();
        second.Items.Should().ContainSingle();
        second.HasMore.Should().BeFalse();

        (await fan.GetAsync(new Uri("/events?pageSize=500", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_event_page_links_to_a_seat_map_cached_for_good()
    {
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));
        var venueId = await backOffice.ImportVenueAsync(Venues.Small(organizerId, rows: 2, seatsPerRow: 3));
        var eventId = await backOffice.CreateEventAsync(new CreateEvent(
            organizerId,
            venueId,
            "Two-tier gig",
            DateTimeOffset.UtcNow.AddMonths(1),
            "EUR",
            [new PriceCategoryInput("Front", 4_000), new PriceCategoryInput("Back", 2_000)],
            [new PriceAssignmentInput("A", "Front", ["1"]), new PriceAssignmentInput("A", "Back")]));

        using var fan = system.PublicApi();
        (await fan.GetAsync(new Uri($"/events/{eventId}", UriKind.Relative), CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "drafts are not public");

        (await backOffice.PublishAsync(eventId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var pageResponse = await fan.GetAsync(new Uri($"/events/{eventId}", UriKind.Relative), CancellationToken);
        pageResponse.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromSeconds(30));
        var page = (await pageResponse.Content.ReadFromJsonAsync<EventPage>(CancellationToken))!;
        page.PriceCategories.Select(c => (c.Name, c.PriceMinorUnits)).Should().Equal(("Front", 4_000L), ("Back", 2_000L));
        page.SalesPhase.Should().Be("NotScheduled");
        page.SeatMapUrl.Should().MatchRegex($"^/events/{eventId}/seat-map/[0-9a-f]{{12}}$");

        var mapResponse = await fan.GetAsync(new Uri(page.SeatMapUrl, UriKind.Relative), CancellationToken);
        mapResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        mapResponse.Headers.CacheControl!.ToString().Should().Contain("immutable").And.Contain("max-age=31536000");

        var map = (await mapResponse.Content.ReadFromJsonAsync<SeatMap>(CancellationToken))!;
        var front = page.PriceCategories.Single(c => c.Name == "Front").PriceCategoryId;
        var seats = map.Sections.Should().ContainSingle().Subject.Seats;
        seats.Select(s => (s.Row, s.Number, s.X, s.Y)).Should().Equal(("1", 1, 0, 0), ("1", 2, 1, 0), ("1", 3, 2, 0), ("2", 1, 0, 1), ("2", 2, 1, 1), ("2", 3, 2, 1));
        seats.Where(s => s.Row == "1").Should().OnlyContain(s => s.PriceCategoryId == front);

        // Seat IDs are the ones Inventory sells, so the client can join availability on them.
        await system.DeliverMessagesAsync();
        var availability = await fan.GetFromJsonAsync<Availability>(new Uri($"/events/{eventId}/availability", UriKind.Relative), CancellationToken);
        availability!.AvailableSeatIds.Should().BeEquivalentTo(seats.Select(s => s.SeatId));

        // Prices aren't in the seat map, so changing them leaves its URL valid.
        var back = page.PriceCategories.Single(c => c.Name == "Back").PriceCategoryId;
        (await backOffice.PutAsJsonAsync(new Uri($"/events/{eventId}/prices", UriKind.Relative), new ChangePricesRequest([new PriceChange(back, 1_000)]), CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = (await fan.GetFromJsonAsync<EventPage>(new Uri($"/events/{eventId}", UriKind.Relative), CancellationToken))!;
        after.SeatMapUrl.Should().Be(page.SeatMapUrl);
        after.PriceCategories.Single(c => c.Name == "Back").PriceMinorUnits.Should().Be(1_000);

        (await fan.GetAsync(new Uri($"/events/{eventId}/seat-map/000000000000", UriKind.Relative), CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<Guid> PublishedEventAsync(HttpClient backOffice, Guid organizerId, Guid venueId, string name, int daysAhead)
    {
        var eventId = await backOffice.CreateEventAsync(BackOffice.SingleCategoryEvent(organizerId, venueId, ["A"]) with
        {
            Name = name,
            StartsAt = DateTimeOffset.UtcNow.AddDays(daysAhead),
        });
        (await backOffice.ScheduleOnSaleAsync(eventId, DateTimeOffset.UtcNow)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await backOffice.PublishAsync(eventId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return eventId;
    }

    private static async Task<EventListPage> ListAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<EventListPage>(new Uri("/events" + query, UriKind.Relative), CancellationToken))!;
}
