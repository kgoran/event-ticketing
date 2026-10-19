using EventTicketing.FanSite.Seating;

namespace EventTicketing.FanSite.Tests;

public sealed class SeatMapModelTests
{
    [Fact]
    public void Seats_get_labels_and_the_event_pages_prices_through_their_price_category()
    {
        var map = SeatMaps.Map();
        var model = SeatMapModel.From(map, SeatMaps.Page());

        var front = model.Seat(map.SeatId("A", "1", 2))!;
        front.Label.Should().Be("Tribune A · Row 1 · Seat 2");
        front.Category.Name.Should().Be("Premium");
        front.Category.PriceMinorUnits.Should().Be(9_000);

        model.Seat(map.SeatId("B", "2", 4))!.Category.PriceMinorUnits.Should().Be(5_000);
        model.Seat(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void Each_price_category_keeps_its_place_in_the_event_page_so_its_colour_is_stable()
    {
        var model = SeatMapModel.From(SeatMaps.Map(), SeatMaps.Page());

        model.Categories.Select(category => (category.Name, category.Index))
            .Should().Equal(("Premium", 0), ("Standard", 1), ("Floor", 2));
    }

    [Fact]
    public void A_standing_area_spans_the_seating_and_fills_the_room_in_front_of_it()
    {
        var model = SeatMapModel.From(SeatMaps.Map(), SeatMaps.Page());

        var floor = model.Section("FLOOR")!;
        floor.IsStanding.Should().BeTrue();
        floor.Category!.Name.Should().Be("Floor");

        // Seats run from x 0 to 9 (10 wide) and start at y 12: the floor ends two units before them.
        floor.Area.Should().Be(new Area(0, 0, 10, 10));
    }

    [Fact]
    public void The_view_box_shows_every_section_with_room_for_their_names()
    {
        var model = SeatMapModel.From(SeatMaps.Map(), SeatMaps.Page());

        model.ViewBox.X.Should().BeLessThan(0);
        model.ViewBox.Y.Should().BeLessThan(-1.5);
        (model.ViewBox.X + model.ViewBox.Width).Should().BeGreaterThan(10);
        (model.ViewBox.Y + model.ViewBox.Height).Should().BeGreaterThan(14);
    }
}
