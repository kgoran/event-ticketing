using System.Globalization;
using NodaTime;
using NodaTime.Text;

namespace EventTicketing.Notifications.Email;

/// <summary>How emails write times and amounts. The same rules as the fan site's, which is another deployable.</summary>
public static class EmailFormats
{
    private static readonly LocalDateTimePattern EventTime = LocalDateTimePattern.CreateWithInvariantCulture("dddd d MMMM yyyy, HH:mm");

    /// <summary>The time as the venue's clock shows it (times are UTC plus the venue's IANA zone, ADR-0024).</summary>
    public static string AtVenue(DateTimeOffset instant, string timeZone)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) ?? DateTimeZone.Utc;
        return EventTime.Format(Instant.FromDateTimeOffset(instant).InZone(zone).LocalDateTime);
    }

    /// <summary>"90.00 EUR": two decimals, like EUR, the demo's currency.</summary>
    public static string Money(long minorUnits, string currency) =>
        string.Create(CultureInfo.InvariantCulture, $"{minorUnits / 100m:0.00} {currency}");

    public static string RefundExplanation(string reason) => reason switch
    {
        "SeatsLost" => "Your payment arrived after your hold had expired and someone else took a seat, so we couldn't sell you the seats.",
        "PaidAfterClose" => "Your payment arrived after the time to pay had run out, so we couldn't sell you the seats.",
        _ => "We couldn't complete your order.",
    };
}
