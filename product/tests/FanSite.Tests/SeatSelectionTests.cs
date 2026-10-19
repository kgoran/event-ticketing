using EventTicketing.FanSite.Seating;

namespace EventTicketing.FanSite.Tests;

public sealed class SeatSelectionTests
{
    [Fact]
    public void Clicking_a_seat_picks_it_and_clicking_it_again_lets_it_go()
    {
        var selection = new SeatSelection(maximum: 8);
        var seat = Guid.NewGuid();

        selection.Toggle(seat).Should().Be(SelectionChange.Added);
        selection.Contains(seat).Should().BeTrue();

        selection.Toggle(seat).Should().Be(SelectionChange.Removed);
        selection.Seats.Should().BeEmpty();
    }

    [Fact]
    public void A_fan_cant_pick_more_seats_than_one_hold_may_have()
    {
        var selection = new SeatSelection(maximum: 2);
        selection.Toggle(Guid.NewGuid());
        selection.Toggle(Guid.NewGuid());

        var third = Guid.NewGuid();
        selection.Toggle(third).Should().Be(SelectionChange.Full);
        selection.Contains(third).Should().BeFalse();
        selection.Seats.Should().HaveCount(2);
    }

    [Fact]
    public void Seats_someone_else_took_meanwhile_are_dropped_and_counted()
    {
        var selection = new SeatSelection(maximum: 8);
        Guid stillFree = Guid.NewGuid(), taken = Guid.NewGuid();
        selection.Toggle(stillFree);
        selection.Toggle(taken);

        selection.KeepOnly(new HashSet<Guid> { stillFree }).Should().Be(1);

        selection.Seats.Should().Equal(stillFree);
    }
}
