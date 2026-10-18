using System.Text.Json.Serialization;

namespace EventTicketing.Catalog.Venues;

[JsonConverter(typeof(JsonStringEnumConverter<SectionKind>))]
public enum SectionKind
{
    /// <summary>Rows of numbered seats.</summary>
    Seated,

    /// <summary>An area sold by capacity; Inventory turns it into anonymous slots (ADR-0011).</summary>
    Standing,
}

/// <summary>
/// A venue's seat map, imported once by an organizer and never edited: events refer to it, so
/// changing it would change events already on sale. A new layout is a new import.
/// </summary>
internal sealed class Venue
{
    private readonly List<VenueSection> _sections = [];

    private Venue()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizerId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>IANA time zone; times are stored in UTC and shown in the venue's zone (ADR-0024).</summary>
    public string TimeZone { get; private set; } = string.Empty;

    public DateTimeOffset ImportedAt { get; private set; }

    public IReadOnlyList<VenueSection> Sections => _sections;

    public static Venue Import(Guid organizerId, string name, string timeZone, IEnumerable<VenueSectionInput> sections, DateTimeOffset now)
    {
        var venue = new Venue
        {
            Id = Guid.CreateVersion7(),
            OrganizerId = organizerId,
            Name = name,
            TimeZone = timeZone,
            ImportedAt = now.ToUniversalTime(),
        };

        var position = 0;
        foreach (var section in sections)
        {
            venue._sections.Add(VenueSection.From(venue.Id, position++, section));
        }

        return venue;
    }

    public VenueSection? Section(string code) => _sections.SingleOrDefault(section => section.Code == code);
}

internal sealed class VenueSection
{
    private readonly List<VenueSeat> _seats = [];

    private VenueSection()
    {
    }

    public Guid Id { get; private set; }

    public Guid VenueId { get; private set; }

    /// <summary>Short and unique within the venue, e.g. "A" or "FLOOR"; events refer to sections by it.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public SectionKind Kind { get; private set; }

    /// <summary>Places in a standing area; null for seated sections.</summary>
    public int? Capacity { get; private set; }

    /// <summary>Top-left corner on the seat map, in seat-sized grid units.</summary>
    public int X { get; private set; }

    public int Y { get; private set; }

    /// <summary>Order in the import file, which is also the order the seat map lists sections in.</summary>
    public int Position { get; private set; }

    public IReadOnlyList<VenueSeat> Seats => _seats;

    public int PlaceCount => Kind == SectionKind.Standing ? Capacity ?? 0 : _seats.Count;

    public bool HasRow(string label) => _seats.Any(seat => seat.Row == label);

    public static VenueSection From(Guid venueId, int position, VenueSectionInput input)
    {
        var section = new VenueSection
        {
            Id = Guid.CreateVersion7(),
            VenueId = venueId,
            Code = input.Code,
            Name = input.Name,
            Kind = input.Kind,
            Capacity = input.Kind == SectionKind.Standing ? input.Capacity : null,
            X = input.X,
            Y = input.Y,
            Position = position,
        };

        if (input.Kind == SectionKind.Seated)
        {
            section.AddSeats(input.Rows ?? []);
        }

        return section;
    }

    /// <summary>
    /// Seats get grid positions (row by row, left to right) and a rank for "best available": front
    /// rows first, and within a row the seats nearest the middle first.
    /// </summary>
    private void AddSeats(IReadOnlyList<VenueRowInput> rows)
    {
        var placed = new List<(VenueRowInput Row, int RowIndex, int SeatIndex)>();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            for (var seatIndex = 0; seatIndex < rows[rowIndex].Seats; seatIndex++)
            {
                placed.Add((rows[rowIndex], rowIndex, seatIndex));
            }
        }

        var ranked = placed
            .OrderBy(seat => seat.RowIndex)
            .ThenBy(seat => Math.Abs((2 * seat.SeatIndex) - (seat.Row.Seats - 1)))
            .ThenBy(seat => seat.SeatIndex);

        var rank = 1;
        foreach (var (row, rowIndex, seatIndex) in ranked)
        {
            _seats.Add(new VenueSeat(
                Guid.CreateVersion7(),
                Id,
                row.Label,
                row.FirstNumber + seatIndex,
                rank++,
                X + seatIndex,
                Y + rowIndex));
        }
    }
}

/// <summary>
/// One seat of the map. Its ID is the seat's identity in every event at this venue, so Inventory's
/// availability and this geometry join on it in the client (ADR-0007).
/// </summary>
internal sealed record VenueSeat(Guid Id, Guid SectionId, string Row, int Number, int Rank, int X, int Y);
