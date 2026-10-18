using EventTicketing.Catalog.Venues;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Catalog.Events;

/// <summary>
/// The stored lifecycle. Draft → Published; cancellation joins later (EventCancelled). Whether an
/// event is on sale is not a stored state but a time window, <see cref="Event.SalesPhaseAt"/>.
/// </summary>
public enum EventStatus
{
    Draft,
    Published,
}

/// <summary>Where a published event stands in its sales window [OnSaleAt, StartsAt).</summary>
public enum SalesPhase
{
    /// <summary>Not published, or published without an on-sale time.</summary>
    NotScheduled,
    Scheduled,
    OnSale,
    Ended,
}

/// <summary>
/// An event an organizer sells tickets for at one of its venues: price categories, which category
/// each seat is in, and the on-sale schedule. Its state machine:
/// <code>
///   Draft ──Publish──► Published
///     │ ScheduleOnSale, ChangePrices   │ ScheduleOnSale (until sales start) → OnSaleScheduled
///     │ (no events)                    │ ChangePrices (until the event starts) → PricesChanged
/// </code>
/// </summary>
internal sealed class Event
{
    private readonly List<PriceCategory> _priceCategories = [];
    private readonly List<PriceAssignment> _priceAssignments = [];

    private Event()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizerId { get; private set; }

    public Guid VenueId { get; private set; }

    /// <summary>Copied from the venue, which never changes, so browsing and search need no join.</summary>
    public string VenueName { get; private set; } = string.Empty;

    /// <summary>The venue's IANA time zone, copied like <see cref="VenueName"/>.</summary>
    public string TimeZone { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset StartsAt { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public EventStatus Status { get; private set; }

    public DateTimeOffset? OnSaleAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Content hash of the published seat map; part of its URL, so the map can be cached for good.</summary>
    public string? SeatMapVersion { get; private set; }

    public IReadOnlyList<PriceCategory> PriceCategories => _priceCategories;

    public IReadOnlyList<PriceAssignment> PriceAssignments => _priceAssignments;

    /// <summary>
    /// Creates a Draft event. Every place in the venue must end up in exactly one price category:
    /// a row rule wins over its section's rule, and standing areas take section rules only.
    /// </summary>
    public static Result<Event> Create(
        Venue venue,
        string name,
        DateTimeOffset startsAt,
        CurrencyCode currency,
        IReadOnlyList<PriceCategoryInput> priceCategories,
        IReadOnlyList<PriceAssignmentInput> seating)
    {
        var @event = new Event
        {
            Id = Guid.CreateVersion7(),
            OrganizerId = venue.OrganizerId,
            VenueId = venue.Id,
            VenueName = venue.Name,
            TimeZone = venue.TimeZone,
            Name = name,
            StartsAt = startsAt.ToUniversalTime(),
            Currency = currency.Value,
            Status = EventStatus.Draft,
        };

        foreach (var category in priceCategories)
        {
            @event._priceCategories.Add(new PriceCategory(Guid.CreateVersion7(), @event.Id, category.Name, category.PriceMinorUnits));
        }

        var errors = new List<FieldError>();
        foreach (var rule in seating)
        {
            var section = venue.Section(rule.Section);
            var category = @event._priceCategories.Single(c => c.Name == rule.PriceCategory);
            if (section is null)
            {
                errors.Add(new FieldError(nameof(CreateEvent.Seating), $"The venue has no section '{rule.Section}'."));
                continue;
            }

            if (rule.Rows is not { Count: > 0 })
            {
                @event._priceAssignments.Add(new PriceAssignment(Guid.CreateVersion7(), @event.Id, section.Code, null, category.Id));
                continue;
            }

            if (section.Kind == SectionKind.Standing)
            {
                errors.Add(new FieldError(nameof(CreateEvent.Seating), $"Section '{section.Code}' is a standing area and has no rows."));
                continue;
            }

            foreach (var row in rule.Rows)
            {
                if (!section.HasRow(row))
                {
                    errors.Add(new FieldError(nameof(CreateEvent.Seating), $"Section '{section.Code}' has no row '{row}'."));
                    continue;
                }

                @event._priceAssignments.Add(new PriceAssignment(Guid.CreateVersion7(), @event.Id, section.Code, row, category.Id));
            }
        }

        if (errors.Count == 0)
        {
            errors.AddRange(venue.Sections
                .SelectMany(section => section.Kind == SectionKind.Standing
                    ? [(section.Code, Row: (string?)null)]
                    : section.Seats.Select(seat => seat.Row).Distinct().Select(row => (section.Code, Row: (string?)row)))
                .Where(place => @event.PriceCategoryIdFor(place.Code, place.Row) is null)
                .Select(place => new FieldError(
                    nameof(CreateEvent.Seating),
                    place.Row is null
                        ? $"Section '{place.Code}' has no price category."
                        : $"Section '{place.Code}' row '{place.Row}' has no price category.")));
        }

        return errors.Count == 0 ? Result.Success(@event) : Result.Failure<Event>(new ValidationError(errors));
    }

    /// <summary>The category of the seats in a row (or of a standing area, when <paramref name="row"/> is null).</summary>
    public Guid? PriceCategoryIdFor(string section, string? row) =>
        _priceAssignments.SingleOrDefault(a => a.Section == section && row is not null && a.Row == row)?.PriceCategoryId
        ?? _priceAssignments.SingleOrDefault(a => a.Section == section && a.Row is null)?.PriceCategoryId;

    public SalesPhase SalesPhaseAt(DateTimeOffset now) => SalesPhaseOf(Status, OnSaleAt, StartsAt, now);

    /// <summary>The same rule for queries that project columns instead of loading the event.</summary>
    public static SalesPhase SalesPhaseOf(EventStatus status, DateTimeOffset? onSaleAt, DateTimeOffset startsAt, DateTimeOffset now) =>
        status != EventStatus.Published || onSaleAt is null ? SalesPhase.NotScheduled
        : now >= startsAt ? SalesPhase.Ended
        : now >= onSaleAt ? SalesPhase.OnSale
        : SalesPhase.Scheduled;

    public Result Publish(DateTimeOffset now, string seatMapVersion)
    {
        if (Status != EventStatus.Draft)
        {
            return Result.Failure(Error.Conflict("event.already_published", "The event is already published."));
        }

        if (StartsAt <= now)
        {
            return Result.Failure(Error.Conflict("event.started", "The event has already started."));
        }

        Status = EventStatus.Published;
        PublishedAt = now.ToUniversalTime();
        SeatMapVersion = seatMapVersion;
        return Result.Success();
    }

    /// <summary>
    /// Sets when sales start. A time in the past means "now". Once sales have started the time can't
    /// move, because fans may already hold seats.
    /// </summary>
    public Result ScheduleOnSale(DateTimeOffset onSaleAt, DateTimeOffset now)
    {
        if (SalesPhaseAt(now) is SalesPhase.OnSale or SalesPhase.Ended)
        {
            return Result.Failure(Error.Conflict("event.sales_started", "Sales have already started; the on-sale time can't change."));
        }

        if (onSaleAt >= StartsAt)
        {
            return Result.Failure(Error.Validation("event.on_sale_after_start", "Sales must start before the event does."));
        }

        OnSaleAt = (onSaleAt < now ? now : onSaleAt).ToUniversalTime();
        return Result.Success();
    }

    /// <summary>Changes prices for orders placed from now on; orders keep the price they were placed at.</summary>
    public Result ChangePrices(IReadOnlyList<PriceChange> changes, DateTimeOffset now)
    {
        if (StartsAt <= now)
        {
            return Result.Failure(Error.Conflict("event.started", "The event has already started."));
        }

        var unknown = changes.Where(change => _priceCategories.All(c => c.Id != change.PriceCategoryId)).ToList();
        if (unknown.Count > 0)
        {
            return Result.Failure(new ValidationError(
                [.. unknown.Select(change => new FieldError("Prices", $"The event has no price category {change.PriceCategoryId}."))]));
        }

        foreach (var change in changes)
        {
            _priceCategories.Single(c => c.Id == change.PriceCategoryId).PriceMinorUnits = change.PriceMinorUnits;
        }

        return Result.Success();
    }
}

internal sealed class PriceCategory(Guid id, Guid eventId, string name, long priceMinorUnits)
{
    public Guid Id { get; private set; } = id;

    public Guid EventId { get; private set; } = eventId;

    public string Name { get; private set; } = name;

    public long PriceMinorUnits { get; set; } = priceMinorUnits;
}

/// <summary>Which price category a section's places are in, or one row's seats when <see cref="Row"/> is set.</summary>
internal sealed record PriceAssignment(Guid Id, Guid EventId, string Section, string? Row, Guid PriceCategoryId);
