using EventTicketing.FanSite.Display;

namespace EventTicketing.FanSite.Tests;

public sealed class DisplayTests
{
    [Theory]
    [InlineData(9_000, "90.00 EUR")]
    [InlineData(2_503, "25.03 EUR")]
    [InlineData(5, "0.05 EUR")]
    public void Amounts_are_written_from_minor_units(long minorUnits, string expected) =>
        Formats.Money(minorUnits, "EUR").Should().Be(expected);

    [Fact]
    public void Times_are_shown_as_the_venues_clock_shows_them()
    {
        // Zagreb is UTC+1 in November and UTC+2 in July.
        Formats.AtVenue(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero), "Europe/Zagreb").Should().Be("Fri 20 Nov 2026, 20:00");
        Formats.AtVenue(new DateTimeOffset(2026, 7, 10, 19, 0, 0, TimeSpan.Zero), "Europe/Zagreb").Should().Be("Fri 10 Jul 2026, 21:00");
    }

    [Fact]
    public void An_unknown_time_zone_falls_back_to_utc_instead_of_failing() =>
        Formats.AtVenue(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero), "Mars/Olympus").Should().Be("Fri 20 Nov 2026, 19:00");

    [Theory]
    [InlineData(65.2, "1:06")]
    [InlineData(60, "1:00")]
    [InlineData(9, "0:09")]
    [InlineData(-3, "0:00")]
    public void Countdowns_round_up_to_the_second_and_stop_at_zero(double seconds, string expected) =>
        Formats.Countdown(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Theory]
    [InlineData("Pending", true)]
    [InlineData("Refunding", true)]
    [InlineData("Confirmed", false)]
    [InlineData("Failed", false)]
    [InlineData("Expired", false)]
    [InlineData("Refunded", false)]
    public void The_return_page_keeps_asking_only_while_an_order_is_still_moving(string status, bool moving) =>
        OrderStatuses.IsMoving(status).Should().Be(moving);

    [Fact]
    public void A_refund_says_why_the_money_goes_back()
    {
        OrderStatuses.Describe("Refunding", "SeatsLost").Explanation.Should().Contain("someone else took a seat");
        OrderStatuses.Describe("Refunded", "PaidAfterClose").Explanation.Should().Contain("time to pay had run out");
    }
}
