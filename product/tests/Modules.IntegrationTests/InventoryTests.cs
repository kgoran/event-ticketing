using System.Net;
using EventTicketing.Inventory.Infrastructure.Endpoints;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>M1a I1: places created from EventPublished, all-or-nothing claims with expiry inside them, hold limits, best available, availability.</summary>
public sealed class InventoryTests(TestSystem system)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Claims_are_all_or_nothing()
    {
        var sale = await system.OnSaleAsync();
        var (s1, s2, s3) = (sale.Seats[0], sale.Seats[1], sale.Seats[2]);
        using var ana = system.PublicApi(Callers.Fan());
        using var ben = system.PublicApi(Callers.Fan());

        (await ana.HoldAsync(sale.EventId, s1, s2)).StatusCode.Should().Be(HttpStatusCode.Created);

        var overlapping = await ben.HoldAsync(sale.EventId, s2, s3);
        overlapping.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await overlapping.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("hold.seats_unavailable");

        // Ben's failed attempt took nothing: s3 is still free, and he can have it alone.
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(s3).And.NotContain([s1, s2]);
        (await ben.HoldAsync(sale.EventId, s3)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task An_expired_seat_can_be_claimed_immediately()
    {
        var sale = await system.OnSaleAsync();
        var seat = sale.Seats[0];
        using var ana = system.PublicApi(Callers.Fan());
        using var ben = system.PublicApi(Callers.Fan());

        var anasHold = await (await ana.HoldAsync(sale.EventId, seat)).PlacedAsync();
        (await ben.HoldAsync(sale.EventId, seat)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Nothing sweeps expired holds: the claim itself treats an expired seat as free (ADR-0011).
        await system.ExpireHoldAsync(anasHold.HoldId);
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(seat);
        (await ben.HoldAsync(sale.EventId, seat)).StatusCode.Should().Be(HttpStatusCode.Created);

        // Ana's lapsed hold doesn't count against her limit either.
        (await ana.HoldAsync(sale.EventId, sale.Seats[1])).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_fan_has_one_active_hold_per_event_until_releasing_it()
    {
        var sale = await system.OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());

        var first = await (await ana.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync();

        var second = await ana.HoldAsync(sale.EventId, sale.Seats[1]);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("hold.active_hold_exists");

        // Only the fan who holds it can release it; anyone else is told it doesn't exist.
        using var ben = system.PublicApi(Callers.Fan());
        (await ben.DeleteAsync(new Uri($"/holds/{first.HoldId}", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await ana.DeleteAsync(new Uri($"/holds/{first.HoldId}", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(sale.Seats[0]);
        (await ana.HoldAsync(sale.EventId, sale.Seats[1])).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Two_simultaneous_holds_by_one_fan_leave_one_hold_and_no_stray_seats()
    {
        var sale = await system.OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan("ana-" + Guid.NewGuid().ToString("N")));

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => ana.HoldAsync(sale.EventId, sale.Seats[i])));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict);

        // The losers' claims were rolled back with their transactions.
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().HaveCount(sale.Seats.Count - 1);
    }

    [Fact]
    public async Task A_hold_has_at_most_eight_seats()
    {
        var sale = await system.OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());

        (await ana.HoldAsync(sale.EventId, [.. sale.Seats.Take(9)])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ana.HoldBestAsync(sale.EventId, "A", 9)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_fan_can_buy_at_most_eight_tickets_for_an_event_across_orders()
    {
        var sale = await system.OnSaleAsync(rows: 2, seatsPerRow: 10);
        using var fan = system.PublicApi(Callers.Fan());

        // Five seats bought and paid for...
        var first = await (await fan.HoldAsync(sale.EventId, [.. sale.Seats.Take(5)])).PlacedAsync();
        var checkout = await fan.CheckoutAsync(first.HoldId);
        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "succeeded");
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();
        (await fan.OrderAsync(checkout.OrderId)).Status.Should().Be("Confirmed");

        // ...leave room for three more, not four: picked seats or best available alike.
        var tooMany = await fan.HoldAsync(sale.EventId, [.. sale.Seats.Skip(5).Take(4)]);
        tooMany.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await tooMany.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("hold.ticket_limit").And.Contain("you already have 5");
        (await fan.HoldBestAsync(sale.EventId, "A", 4)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await (await fan.HoldAsync(sale.EventId, [.. sale.Seats.Skip(5).Take(3)])).PlacedAsync();

        // A refused hold took nothing: someone else can have those seats at once.
        using var other = system.PublicApi(Callers.Fan());
        await (await other.HoldAsync(sale.EventId, sale.Seats[8])).PlacedAsync();
    }

    [Fact]
    public async Task Best_available_takes_the_best_ranked_free_places_of_a_section()
    {
        var sale = await system.OnSaleAsync(withFloor: true);
        using var ana = system.PublicApi(Callers.Fan());
        using var ben = system.PublicApi(Callers.Fan());
        using var cid = system.PublicApi(Callers.Fan());

        // Row 1 of 5 seats: the middle seat first, then its neighbours.
        var best = await (await ana.HoldBestAsync(sale.EventId, "A", 3)).PlacedAsync();
        best.SeatIds.Should().BeEquivalentTo(sale.SeatIdsAt(("1", 3), ("1", 2), ("1", 4)));

        var next = await (await ben.HoldBestAsync(sale.EventId, "A", 2)).PlacedAsync();
        next.SeatIds.Should().BeEquivalentTo(sale.SeatIdsAt(("1", 1), ("1", 5)));

        // Standing places are slots, held the same way.
        var floor = await (await cid.HoldBestAsync(sale.EventId, "FLOOR", 4)).PlacedAsync();
        floor.SeatIds.Should().HaveCount(4);

        var availability = await system.AvailabilityAsync(sale.EventId);
        availability.Sections.Should().ContainEquivalentOf(new SectionAvailability("A", 5, 10));
        availability.Sections.Should().ContainEquivalentOf(new SectionAvailability("FLOOR", 16, 20));
        availability.AvailableSeatIds.Should().HaveCount(5, "standing slots are counted, not listed");

        using var dan = system.PublicApi(Callers.Fan());
        var tooMany = await dan.HoldBestAsync(sale.EventId, "A", 6);
        tooMany.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await system.AvailabilityAsync(sale.EventId)).Sections.Should().ContainEquivalentOf(new SectionAvailability("A", 5, 10));
    }

    [Fact]
    public async Task Nothing_can_be_held_before_sales_start()
    {
        var sale = await system.OnSaleAsync(onSaleAt: DateTimeOffset.UtcNow.AddDays(1));
        using var ana = system.PublicApi(Callers.Fan());

        var early = await ana.HoldAsync(sale.EventId, sale.Seats[0]);
        early.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await early.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("hold.not_on_sale");
        (await system.AvailabilityAsync(sale.EventId)).OnSale.Should().BeFalse();

        // The organizer opens sales now; OnSaleScheduled reaches Inventory.
        using var backOffice = system.BackOfficeApi(Callers.Organizer(sale.OrganizerId));
        (await backOffice.ScheduleOnSaleAsync(sale.EventId, DateTimeOffset.UtcNow)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await system.DeliverMessagesAsync();

        (await system.AvailabilityAsync(sale.EventId)).OnSale.Should().BeTrue();
        (await ana.HoldAsync(sale.EventId, sale.Seats[0])).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Seats_of_another_event_cannot_be_held()
    {
        var first = await system.OnSaleAsync();
        var second = await system.OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());

        (await ana.HoldAsync(first.EventId, second.Seats[0])).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
