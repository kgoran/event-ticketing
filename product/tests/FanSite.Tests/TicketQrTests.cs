using EventTicketing.FanSite.Display;

namespace EventTicketing.FanSite.Tests;

public sealed class TicketQrTests
{
    [Fact]
    public void A_code_becomes_an_svg_that_scales_to_its_box()
    {
        var svg = TicketQr.Svg("eyJhbGciOiJFUzI1NiJ9.eyJ0aWQiOiIxIn0.c2lnbmF0dXJl");

        var openingTag = svg[..(svg.IndexOf('>', StringComparison.Ordinal) + 1)];
        openingTag.Should().StartWith("<svg").And.Contain("viewBox").And.NotContain("width=");
    }

    [Fact]
    public void Different_codes_draw_different_qr_codes() =>
        TicketQr.Svg("ticket-one").Should().NotBe(TicketQr.Svg("ticket-two"));
}
