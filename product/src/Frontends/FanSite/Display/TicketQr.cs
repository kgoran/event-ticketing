using QRCoder;

namespace EventTicketing.FanSite.Display;

/// <summary>Draws a ticket's code as a QR code: an SVG that scales to its box, black on white.</summary>
internal static class TicketQr
{
    /// <summary>
    /// The SVG for <paramref name="code"/>. Error correction M: the code stays readable with a
    /// scratched or creased printout, and a ~250-character JWS still fits a phone screen.
    /// </summary>
    public static string Svg(string code)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(code, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
