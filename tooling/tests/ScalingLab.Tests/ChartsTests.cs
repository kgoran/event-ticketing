namespace EventTicketing.Tooling.ScalingLab.Tests;

/// <summary>The lab's charts: round scales, gaps where nothing was measured, labels that don't overlap.</summary>
public sealed class ChartsTests
{
    [Theory]
    [InlineData(137, 10, 150, 50)]
    [InlineData(0, 20, 20, 5)]
    [InlineData(84, 20, 100, 25)]
    [InlineData(201, 20, 300, 100)]
    [InlineData(3.2, 1, 4, 1)]
    public void Scales_end_on_a_round_number(double max, double minimumTop, double top, double step) =>
        Charts.Scale(max, minimumTop).Should().Be((top, step));

    [Fact]
    public void A_second_without_a_value_breaks_the_line()
    {
        var path = Charts.Path([1, 2, null, 4, null], x: i => i * 10, y: value => 100 - value);

        path.Should().Be("M0 99 L10 98 M30 96");
    }

    [Fact]
    public void End_labels_keep_their_order_and_their_distance()
    {
        var placed = Charts.Spread([50, 52, 200, 51], gap: 14, top: 10, bottom: 180);

        placed[0].Should().Be(50);
        placed[3].Should().Be(64);
        placed[1].Should().Be(78);
        placed[2].Should().Be(180, "a label never leaves the plot");
    }

    [Fact]
    public void Labels_crowded_at_the_bottom_move_up()
    {
        var placed = Charts.Spread([180, 180, 180], gap: 14, top: 10, bottom: 180);

        placed.Should().Equal(152, 166, 180);
    }
}
