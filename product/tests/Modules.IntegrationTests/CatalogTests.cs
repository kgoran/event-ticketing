using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.Catalog.Events;
using EventTicketing.Catalog.Venues;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>M1a C1: venues, events, prices, the on-sale schedule and publishing.</summary>
public sealed class CatalogTests(TestSystem system)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_2000_seat_venue_imports_and_publishes()
    {
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));

        var venueId = await backOffice.ImportVenueAsync(Venues.Arena(organizerId));
        var venue = await backOffice.GetFromJsonAsync<VenueSummary>(new Uri($"/venues/{venueId}", UriKind.Relative), CancellationToken);
        venue!.Places.Should().Be(3_000);
        venue.Sections.Select(s => (s.Code, s.Kind, s.Places)).Should().Equal(
            ("A", "Seated", 500), ("B", "Seated", 500), ("C", "Seated", 500), ("D", "Seated", 500), ("FLOOR", "Standing", 1_000));

        // Rows 1–5 of A are Premium, the rest of the seats Standard, the floor its own category.
        var eventId = await backOffice.CreateEventAsync(new CreateEvent(
            organizerId,
            venueId,
            "Arena concert",
            DateTimeOffset.UtcNow.AddMonths(2),
            "EUR",
            [new PriceCategoryInput("Premium", 9_000), new PriceCategoryInput("Standard", 5_000), new PriceCategoryInput("Floor", 4_000)],
            [
                new PriceAssignmentInput("A", "Premium", ["1", "2", "3", "4", "5"]),
                new PriceAssignmentInput("A", "Standard"),
                new PriceAssignmentInput("B", "Standard"),
                new PriceAssignmentInput("C", "Standard"),
                new PriceAssignmentInput("D", "Standard"),
                new PriceAssignmentInput("FLOOR", "Floor"),
            ]));

        var onSaleAt = DateTimeOffset.UtcNow.AddDays(7);
        (await backOffice.ScheduleOnSaleAsync(eventId, onSaleAt)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await backOffice.PublishAsync(eventId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var details = await backOffice.EventDetailsAsync(eventId);
        details.Status.Should().Be("Published");
        details.SalesPhase.Should().Be("Scheduled");

        // EventPublished carries every seat with its category and rank, and the standing area.
        var published = await OutboxAsync("catalog.event-published", eventId);
        var message = published.Should().ContainSingle().Subject;
        var seats = message.GetProperty("seats").EnumerateArray().ToList();
        seats.Should().HaveCount(2_000);
        message.GetProperty("onSaleAt").GetDateTimeOffset().Should().BeCloseTo(onSaleAt, TimeSpan.FromMilliseconds(1));
        message.GetProperty("timeZone").GetString().Should().Be("Europe/Zagreb");

        var premium = details.PriceCategories.Single(c => c.Name == "Premium").PriceCategoryId;
        seats.Count(seat => seat.GetProperty("priceCategoryId").GetGuid() == premium).Should().Be(5 * 25);

        // Best seat in A: front row, middle of 25.
        var best = seats.Single(seat => seat.GetProperty("section").GetString() == "A" && seat.GetProperty("rank").GetInt32() == 1);
        (best.GetProperty("row").GetString(), best.GetProperty("number").GetInt32()).Should().Be(("1", 13));

        var standing = message.GetProperty("standingAreas").EnumerateArray().Should().ContainSingle().Subject;
        standing.GetProperty("capacity").GetInt32().Should().Be(1_000);

        // Inventory and Orders build their own copies from it.
        await system.DeliverMessagesAsync();
        (await system.ScalarAsync<long>("SELECT count(*) FROM inventory.seats WHERE event_id = @p0", eventId)).Should().Be(2_000);
        (await system.ScalarAsync<long>("SELECT count(*) FROM orders.price_list WHERE event_id = @p0", eventId)).Should().Be(3);
    }

    [Fact]
    public async Task Every_place_needs_a_price_category()
    {
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));
        var venueId = await backOffice.ImportVenueAsync(Venues.Small(organizerId, rows: 3));

        var response = await backOffice.PostAsJsonAsync(
            new Uri("/events", UriKind.Relative),
            new CreateEvent(
                organizerId,
                venueId,
                "Half priced",
                DateTimeOffset.UtcNow.AddMonths(1),
                "EUR",
                [new PriceCategoryInput("Front", 3_000)],
                [new PriceAssignmentInput("A", "Front", ["1", "2"])]),
            CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("Section 'A' row '3' has no price category.");
    }

    [Fact]
    public async Task A_venue_layout_is_validated_on_import()
    {
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));

        var response = await backOffice.PostAsJsonAsync(
            new Uri("/venues", UriKind.Relative),
            new ImportVenue(
                organizerId,
                "Broken",
                "Not/A_Zone",
                [
                    new VenueSectionInput("A", "Seats", SectionKind.Seated, Rows: [new VenueRowInput("1", 10), new VenueRowInput("1", 10)]),
                    new VenueSectionInput("A", "Floor", SectionKind.Standing),
                ]),
            CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        body.Should().Contain("IANA time zone").And.Contain("Section codes must be unique")
            .And.Contain("Row labels must be unique").And.Contain("Capacity");
    }

    [Fact]
    public async Task Another_organizer_can_neither_see_nor_change_the_event()
    {
        var ownerId = Guid.NewGuid();
        using var owner = system.BackOfficeApi(Callers.Organizer(ownerId));
        var venueId = await owner.ImportVenueAsync(Venues.Small(ownerId));
        var eventId = await owner.CreateEventAsync(BackOffice.SingleCategoryEvent(ownerId, venueId, ["A"]));

        var otherId = Guid.NewGuid();
        using var other = system.BackOfficeApi(Callers.Organizer(otherId));

        (await other.GetAsync(new Uri($"/events/{eventId}", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync(new Uri($"/venues/{venueId}", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PublishAsync(eventId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.ScheduleOnSaleAsync(eventId, DateTimeOffset.UtcNow)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Creating an event at someone else's venue: the venue doesn't exist as far as they're concerned.
        var atOwnersVenue = await other.PostAsJsonAsync(
            new Uri("/events", UriKind.Relative), BackOffice.SingleCategoryEvent(otherId, venueId, ["A"]), CancellationToken);
        atOwnersVenue.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Claiming to be the owner doesn't help: the token decides.
        var asOwner = await other.PostAsJsonAsync(
            new Uri("/events", UriKind.Relative), BackOffice.SingleCategoryEvent(ownerId, venueId, ["A"]), CancellationToken);
        asOwner.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await owner.EventDetailsAsync(eventId)).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task Sales_scheduled_in_the_past_open_now_and_then_cannot_move()
    {
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));
        var venueId = await backOffice.ImportVenueAsync(Venues.Small(organizerId));
        var eventId = await backOffice.CreateEventAsync(BackOffice.SingleCategoryEvent(organizerId, venueId, ["A"]));
        (await backOffice.PublishAsync(eventId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await backOffice.EventDetailsAsync(eventId)).SalesPhase.Should().Be("NotScheduled");

        (await backOffice.ScheduleOnSaleAsync(eventId, DateTimeOffset.UtcNow.AddHours(-1))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var details = await backOffice.EventDetailsAsync(eventId);
        details.SalesPhase.Should().Be("OnSale");
        details.OnSaleAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "a time in the past means now");

        var scheduled = await OutboxAsync("catalog.on-sale-scheduled", eventId);
        scheduled.Should().ContainSingle().Which.GetProperty("onSaleAt").GetDateTimeOffset().Should().BeCloseTo(details.OnSaleAt!.Value, TimeSpan.FromMilliseconds(1));

        var moved = await backOffice.ScheduleOnSaleAsync(eventId, DateTimeOffset.UtcNow.AddDays(1));
        moved.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await moved.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("event.sales_started");
    }

    [Fact]
    public async Task Sales_must_start_before_the_event()
    {
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));
        var venueId = await backOffice.ImportVenueAsync(Venues.Small(organizerId));
        var command = BackOffice.SingleCategoryEvent(organizerId, venueId, ["A"]);
        var eventId = await backOffice.CreateEventAsync(command);

        var response = await backOffice.ScheduleOnSaleAsync(eventId, command.StartsAt.AddMinutes(1));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("event.on_sale_after_start");
    }

    [Fact]
    public async Task Price_changes_after_publishing_announce_the_full_price_list()
    {
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));
        var venueId = await backOffice.ImportVenueAsync(Venues.Small(organizerId));
        var eventId = await backOffice.CreateEventAsync(new CreateEvent(
            organizerId,
            venueId,
            "Two prices",
            DateTimeOffset.UtcNow.AddMonths(1),
            "EUR",
            [new PriceCategoryInput("Front", 4_000), new PriceCategoryInput("Back", 2_000)],
            [new PriceAssignmentInput("A", "Front", ["1"]), new PriceAssignmentInput("A", "Back")]));

        var categories = (await backOffice.EventDetailsAsync(eventId)).PriceCategories;
        var front = categories.Single(c => c.Name == "Front").PriceCategoryId;
        var back = categories.Single(c => c.Name == "Back").PriceCategoryId;

        // Before publishing nobody else knows the event, so nothing is announced.
        (await ChangePricesAsync(backOffice, eventId, new PriceChange(front, 4_500))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await OutboxAsync("catalog.prices-changed", eventId)).Should().BeEmpty();

        (await backOffice.PublishAsync(eventId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ChangePricesAsync(backOffice, eventId, new PriceChange(back, 1_500))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var changed = (await OutboxAsync("catalog.prices-changed", eventId)).Should().ContainSingle().Subject;
        changed.GetProperty("priceCategories").EnumerateArray()
            .Select(c => (c.GetProperty("name").GetString(), c.GetProperty("priceMinorUnits").GetInt64()))
            .Should().BeEquivalentTo([("Front", 4_500L), ("Back", 1_500L)]);

        (await ChangePricesAsync(backOffice, eventId, new PriceChange(Guid.NewGuid(), 100))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_event_is_published_once()
    {
        var organizerId = Guid.NewGuid();
        using var backOffice = system.BackOfficeApi(Callers.Organizer(organizerId));
        var venueId = await backOffice.ImportVenueAsync(Venues.Small(organizerId));
        var eventId = await backOffice.CreateEventAsync(BackOffice.SingleCategoryEvent(organizerId, venueId, ["A"]));

        (await backOffice.PublishAsync(eventId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await backOffice.PublishAsync(eventId)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await OutboxAsync("catalog.event-published", eventId)).Should().ContainSingle();
    }

    private static Task<HttpResponseMessage> ChangePricesAsync(HttpClient backOffice, Guid eventId, params PriceChange[] prices) =>
        backOffice.PutAsJsonAsync(new Uri($"/events/{eventId}/prices", UriKind.Relative), new ChangePricesRequest(prices), CancellationToken);

    private async Task<IReadOnlyList<JsonElement>> OutboxAsync(string type, Guid eventId)
    {
        var payloads = await system.QueryAsync<string>(
            "SELECT payload::text FROM catalog.outbox_messages WHERE type = @p0 AND payload->>'eventId' = @p1 ORDER BY occurred_at",
            type,
            eventId.ToString());

        return [.. payloads.Select(payload => JsonDocument.Parse(payload).RootElement)];
    }
}
