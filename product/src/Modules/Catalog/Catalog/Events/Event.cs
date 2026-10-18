using EventTicketing.Shared.Domain;

namespace EventTicketing.Catalog.Events;

public enum EventStatus
{
    Draft,
    Published,
}

/// <summary>An event an organizer sells tickets for, with its seat layout and price categories.</summary>
internal sealed class Event
{
    private readonly List<PriceCategory> _priceCategories = [];
    private readonly List<Seat> _seats = [];

    private Event()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizerId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset StartsAt { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public EventStatus Status { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public IReadOnlyList<PriceCategory> PriceCategories => _priceCategories;

    public IReadOnlyList<Seat> Seats => _seats;

    public static Event Create(
        Guid organizerId,
        string name,
        DateTimeOffset startsAt,
        CurrencyCode currency,
        IEnumerable<PriceCategoryInput> priceCategories,
        IEnumerable<SectionInput> sections)
    {
        var @event = new Event
        {
            Id = Guid.CreateVersion7(),
            OrganizerId = organizerId,
            Name = name,
            StartsAt = startsAt.ToUniversalTime(),
            Currency = currency.Value,
            Status = EventStatus.Draft,
        };

        foreach (var category in priceCategories)
        {
            @event._priceCategories.Add(new PriceCategory(Guid.CreateVersion7(), @event.Id, category.Name, category.PriceMinorUnits));
        }

        // A simple layout: each section has rows 1..N with seats 1..M, all in one price category.
        foreach (var section in sections)
        {
            var category = @event._priceCategories.Single(c => c.Name == section.PriceCategory);
            for (var row = 1; row <= section.Rows; row++)
            {
                for (var number = 1; number <= section.SeatsPerRow; number++)
                {
                    @event._seats.Add(new Seat(Guid.CreateVersion7(), @event.Id, section.Name, row.ToString(System.Globalization.CultureInfo.InvariantCulture), number, category.Id));
                }
            }
        }

        return @event;
    }

    public Result Publish(DateTimeOffset now)
    {
        if (Status != EventStatus.Draft)
        {
            return Result.Failure(Error.Conflict("event.already_published", "The event is already published."));
        }

        Status = EventStatus.Published;
        PublishedAt = now.ToUniversalTime();
        return Result.Success();
    }
}

internal sealed record PriceCategory(Guid Id, Guid EventId, string Name, long PriceMinorUnits);

internal sealed record Seat(Guid Id, Guid EventId, string Section, string Row, int Number, Guid PriceCategoryId);
