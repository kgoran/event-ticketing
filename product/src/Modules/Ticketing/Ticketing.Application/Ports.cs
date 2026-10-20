using EventTicketing.Ticketing.Domain;

namespace EventTicketing.Ticketing.Application;

public interface ITicketRepository
{
    void Add(Ticket ticket);

    Task<bool> AnyForOrderAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <summary>Ticketing's copy of the events it issues tickets for.</summary>
public interface ITicketedEvents
{
    Task<TicketedEvent?> FindAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>Stores the event, its seats' labels and its standing areas once; a repeated call changes nothing.</summary>
    Task AddAsync(TicketedEvent ticketedEvent, IReadOnlyCollection<SeatLabel> seats, IReadOnlyCollection<StandingLabel> standing, CancellationToken cancellationToken);

    /// <summary>The label of each of an order's places: by seat ID, or for a standing slot by its price category.</summary>
    Task<IReadOnlyDictionary<Guid, string>> LabelsAsync(Guid eventId, IReadOnlyCollection<(Guid SeatId, Guid PriceCategoryId)> places, CancellationToken cancellationToken);
}

/// <summary>The events' signing keys (ADR-0051).</summary>
public interface IEventKeys
{
    /// <summary>Creates the event's key pair, unless it has one: safe to repeat.</summary>
    Task EnsureForAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>The key new tickets for the event are signed with; null if the event has none yet.</summary>
    Task<EventKey?> CurrentAsync(Guid eventId, CancellationToken cancellationToken);
}

/// <summary>Turns a ticket into its signed code: a JWS (ES256) over the ticket ID, event ID and version.</summary>
public interface ITicketSigner
{
    string Sign(TicketSpec ticket, EventKey key);
}
