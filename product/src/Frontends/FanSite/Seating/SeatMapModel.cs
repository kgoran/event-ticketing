using EventTicketing.FanSite.PublicApi.Models;

namespace EventTicketing.FanSite.Seating;

/// <summary>A price category with its current price; <see cref="Index"/> picks its colour on the map.</summary>
public sealed record CategoryView(Guid PriceCategoryId, string Name, long PriceMinorUnits, int Index);

public sealed record SeatView(Guid SeatId, string Label, int X, int Y, CategoryView Category);

/// <summary>An area on the map, in the seat map's grid units (one seat is one unit).</summary>
public readonly record struct Area(double X, double Y, double Width, double Height);

/// <summary>A seated section with its seats, or a standing area (no seats; places are counted).</summary>
public sealed record SectionView(string Code, string Name, bool IsStanding, Area Area, IReadOnlyList<SeatView> Seats, CategoryView? Category);

/// <summary>
/// The seat map as the site draws it: Catalog's geometry (cached for good, without prices) joined
/// with the event page's prices on the price category (ADR-0007 rule 4). Inventory's availability
/// is joined later, on the seat ID, because it changes all the time.
/// </summary>
public sealed class SeatMapModel
{
    private const double Margin = 1.5;
    private readonly Dictionary<Guid, SeatView> _seats;

    private SeatMapModel(IReadOnlyList<CategoryView> categories, IReadOnlyList<SectionView> sections, Area viewBox)
    {
        Categories = categories;
        Sections = sections;
        ViewBox = viewBox;
        _seats = sections.SelectMany(section => section.Seats).ToDictionary(seat => seat.SeatId);
    }

    public IReadOnlyList<CategoryView> Categories { get; }

    public IReadOnlyList<SectionView> Sections { get; }

    /// <summary>The part of the grid to show, with room for the section names above the sections.</summary>
    public Area ViewBox { get; }

    public SeatView? Seat(Guid seatId) => _seats.GetValueOrDefault(seatId);

    public SectionView? Section(string? code) => Sections.FirstOrDefault(section => section.Code == code);

    public static SeatMapModel From(SeatMap map, EventPage page)
    {
        var categories = (page.PriceCategories ?? [])
            .Select((category, index) => new CategoryView(category.PriceCategoryId!.Value, category.Name ?? string.Empty, category.PriceMinorUnits ?? 0, index))
            .ToDictionary(category => category.PriceCategoryId);

        var seated = (map.Sections ?? []).Where(section => section.Kind != "Standing" && section.Seats is { Count: > 0 }).ToList();
        var seats = seated.SelectMany(section => section.Seats!).ToList();
        var seatedArea = seats.Count == 0
            ? new Area(0, 0, 20, 0)
            : new Area(seats.Min(s => s.X ?? 0), seats.Min(s => s.Y ?? 0), seats.Max(s => s.X ?? 0) + 1 - seats.Min(s => s.X ?? 0), seats.Max(s => s.Y ?? 0) + 1 - seats.Min(s => s.Y ?? 0));

        var sections = (map.Sections ?? []).Select(section => section.Kind == "Standing"
                ? Standing(section, seatedArea, categories)
                : Seated(section, categories))
            .ToList();

        return new SeatMapModel([.. categories.Values], sections, ViewBoxAround(sections));
    }

    private static SectionView Seated(SeatMapSection section, Dictionary<Guid, CategoryView> categories)
    {
        var name = section.Name ?? section.Code ?? string.Empty;
        var seats = (section.Seats ?? [])
            .Select(seat => new SeatView(
                seat.SeatId!.Value,
                $"{name} · Row {seat.Row} · Seat {seat.Number}",
                seat.X ?? 0,
                seat.Y ?? 0,
                categories[seat.PriceCategoryId!.Value]))
            .ToList();

        var area = seats.Count == 0
            ? new Area(section.X ?? 0, section.Y ?? 0, 1, 1)
            : new Area(seats.Min(s => s.X), seats.Min(s => s.Y), seats.Max(s => s.X) + 1 - seats.Min(s => s.X), seats.Max(s => s.Y) + 1 - seats.Min(s => s.Y));

        return new SectionView(section.Code ?? string.Empty, name, IsStanding: false, area, seats, Category: null);
    }

    /// <summary>
    /// A standing area has a position but no size in the venue's import, so it is drawn as wide as
    /// the seating and as deep as the room in front of it (at least a few units).
    /// </summary>
    private static SectionView Standing(SeatMapSection section, Area seatedArea, Dictionary<Guid, CategoryView> categories)
    {
        double x = section.X ?? 0, y = section.Y ?? 0;
        var width = Math.Max(seatedArea.X + seatedArea.Width - x, 10);
        var height = seatedArea.Y - y - 2 >= 4 ? seatedArea.Y - y - 2 : 6;
        var category = section.PriceCategoryId is { } id ? categories.GetValueOrDefault(id) : null;

        return new SectionView(section.Code ?? string.Empty, section.Name ?? section.Code ?? string.Empty, IsStanding: true, new Area(x, y, width, height), [], category);
    }

    private static Area ViewBoxAround(IReadOnlyList<SectionView> sections)
    {
        if (sections.Count == 0)
        {
            return new Area(0, 0, 20, 10);
        }

        var left = sections.Min(section => section.Area.X) - Margin;
        var top = sections.Min(section => section.Area.Y) - Margin - 1.5; // the section names sit above
        var right = sections.Max(section => section.Area.X + section.Area.Width) + Margin;
        var bottom = sections.Max(section => section.Area.Y + section.Area.Height) + Margin;
        return new Area(left, top, right - left, bottom - top);
    }
}
