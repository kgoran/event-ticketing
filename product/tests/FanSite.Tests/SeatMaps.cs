using EventTicketing.FanSite.PublicApi.Models;

namespace EventTicketing.FanSite.Tests;

/// <summary>A small venue as PublicApi returns it: a standing floor in front of two short tribunes.</summary>
internal static class SeatMaps
{
    public static readonly Guid Premium = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    public static readonly Guid Standard = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    public static readonly Guid Floor = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    public static EventPage Page() => new()
    {
        EventId = Guid.NewGuid(),
        Name = "Test Concert",
        Currency = "EUR",
        PriceCategories =
        [
            new PublicPriceCategory { PriceCategoryId = Premium, Name = "Premium", PriceMinorUnits = 9_000 },
            new PublicPriceCategory { PriceCategoryId = Standard, Name = "Standard", PriceMinorUnits = 5_000 },
            new PublicPriceCategory { PriceCategoryId = Floor, Name = "Floor", PriceMinorUnits = 3_500 },
        ],
    };

    /// <summary>Floor at (0, 0); tribune A at x 0–3 and B at x 6–9, rows y 12–13; row 1 Premium.</summary>
    public static SeatMap Map() => new()
    {
        Sections =
        [
            new SeatMapSection { Code = "FLOOR", Name = "Floor", Kind = "Standing", X = 0, Y = 0, Capacity = 100, PriceCategoryId = Floor, Seats = [] },
            Tribune("A", "Tribune A", left: 0),
            Tribune("B", "Tribune B", left: 6),
        ],
    };

    private static SeatMapSection Tribune(string code, string name, int left) => new()
    {
        Code = code,
        Name = name,
        Kind = "Seated",
        X = left,
        Y = 12,
        Seats =
        [
            .. from row in new[] { 1, 2 }
               from number in Enumerable.Range(1, 4)
               select new SeatMapSeat
               {
                   SeatId = Guid.NewGuid(),
                   Row = row.ToString(System.Globalization.CultureInfo.InvariantCulture),
                   Number = number,
                   X = left + number - 1,
                   Y = 12 + row - 1,
                   PriceCategoryId = row == 1 ? Premium : Standard,
               },
        ],
    };

    public static Guid SeatId(this SeatMap map, string section, string row, int number) =>
        map.Sections!.Single(s => s.Code == section).Seats!.Single(s => s.Row == row && s.Number == number).SeatId!.Value;
}
