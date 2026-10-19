using EventTicketing.FanSite.Holds;
using EventTicketing.FanSite.Seating;

namespace EventTicketing.FanSite.Tests;

public sealed class MyHoldTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Held_seats_are_labelled_from_the_map_and_listed_row_by_row()
    {
        var map = SeatMaps.Map();
        var model = SeatMapModel.From(map, SeatMaps.Page());

        // PublicApi returns the places in any order.
        var places = MyHold.PlacesFrom([map.SeatId("A", "2", 1), map.SeatId("A", "1", 3), map.SeatId("A", "1", 2)], model, standingSection: null);

        places.Select(place => place.Label).Should().Equal(
            "Tribune A · Row 1 · Seat 2",
            "Tribune A · Row 1 · Seat 3",
            "Tribune A · Row 2 · Seat 1");
        places.Sum(place => place.PriceMinorUnits).Should().Be(9_000 + 9_000 + 5_000);
    }

    [Fact]
    public void Places_in_a_standing_area_are_not_on_the_map_so_they_are_labelled_and_priced_by_the_area()
    {
        var model = SeatMapModel.From(SeatMaps.Map(), SeatMaps.Page());

        var places = MyHold.PlacesFrom([Guid.NewGuid(), Guid.NewGuid()], model, standingSection: "FLOOR");

        places.Should().AllSatisfy(place =>
        {
            place.Label.Should().Be("Floor · standing");
            place.PriceMinorUnits.Should().Be(3_500);
        });
    }

    [Fact]
    public void A_hold_lapses_at_its_expiry_unless_its_fan_has_checked_out()
    {
        var hold = new MyHold(Guid.NewGuid(), Guid.NewGuid(), [new HeldPlace(Guid.NewGuid(), "A 1 1", 9_000)], ExpiresAt: Now.AddMinutes(1));

        hold.RemainingAt(Now).Should().Be(TimeSpan.FromMinutes(1));
        hold.HasLapsedAt(Now.AddSeconds(59)).Should().BeFalse();
        hold.HasLapsedAt(Now.AddMinutes(1)).Should().BeTrue();
        hold.RemainingAt(Now.AddMinutes(2)).Should().Be(TimeSpan.Zero);

        // In checkout, the order's deadline decides; the page asks PublicApi about the order instead.
        (hold with { OrderId = Guid.NewGuid() }).HasLapsedAt(Now.AddMinutes(5)).Should().BeFalse();
    }
}
