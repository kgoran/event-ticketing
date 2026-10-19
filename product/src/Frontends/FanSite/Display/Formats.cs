using System.Globalization;
using NodaTime;
using NodaTime.Text;

namespace EventTicketing.FanSite.Display;

/// <summary>How the site writes amounts, times and countdowns.</summary>
internal static class Formats
{
    private static readonly LocalDateTimePattern EventTime = LocalDateTimePattern.CreateWithInvariantCulture("ddd d MMM yyyy, HH:mm");

    /// <summary>
    /// "90.00 EUR". Assumes two decimals, like EUR, the demo's currency. A currency with other
    /// minor units (JPY has none, BHD three) would need ISO 4217's exponent from the API.
    /// </summary>
    public static string Money(long minorUnits, string? currency) =>
        string.Create(CultureInfo.InvariantCulture, $"{minorUnits / 100m:0.00} {currency}").TrimEnd();

    /// <summary>
    /// An instant as the clock shows it at the venue, e.g. "Fri 20 Nov 2026, 20:00": times are stored
    /// in UTC plus the venue's IANA time zone (ADR-0024). NodaTime's own time-zone database works
    /// the same on every OS and with invariant globalization.
    /// </summary>
    public static string AtVenue(DateTimeOffset? instant, string? timeZone)
    {
        if (instant is null)
        {
            return string.Empty;
        }

        var zone = timeZone is null ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone);
        return EventTime.Format(Instant.FromDateTimeOffset(instant.Value).InZone(zone ?? DateTimeZone.Utc).LocalDateTime);
    }

    /// <summary>"4:05" for a hold's or a checkout's time left; never negative.</summary>
    public static string Countdown(TimeSpan remaining)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    public static string SalesPhase(string? phase, DateTimeOffset? onSaleAt, string? timeZone) => phase switch
    {
        "OnSale" => "On sale",
        "Scheduled" => $"On sale from {AtVenue(onSaleAt, timeZone)}",
        "Ended" => "Sales have ended",
        _ => "Not on sale yet",
    };
}
