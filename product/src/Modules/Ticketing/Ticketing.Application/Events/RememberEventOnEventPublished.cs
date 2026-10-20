using System.Globalization;
using EventTicketing.Catalog.Contracts;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Ticketing.Domain;

namespace EventTicketing.Ticketing.Application.Events;

/// <summary>
/// When Catalog publishes an event, Ticketing keeps what its tickets will need (name, time, a label
/// per place) and creates the event's signing key, before any order for it can be confirmed
/// (ADR-0051). Safe to repeat.
/// </summary>
internal sealed class RememberEventOnEventPublished(ITicketedEvents events, IEventKeys keys) : IIntegrationEventHandler<EventPublished>
{
    public async Task HandleAsync(EventPublished integrationEvent, CancellationToken cancellationToken)
    {
        var eventId = integrationEvent.EventId;

        await events.AddAsync(
            new TicketedEvent(eventId, integrationEvent.Name, integrationEvent.StartsAt, integrationEvent.TimeZone),
            [.. integrationEvent.Seats.Select(seat => new SeatLabel(eventId, seat.SeatId, SeatText(seat)))],
            [.. integrationEvent.StandingAreas.Select(area => new StandingLabel(eventId, area.PriceCategoryId, $"{area.Section} · standing"))],
            cancellationToken);

        await keys.EnsureForAsync(eventId, cancellationToken);
    }

    private static string SeatText(PublishedSeat seat) =>
        string.Create(CultureInfo.InvariantCulture, $"{seat.Section} · Row {seat.Row} · Seat {seat.Number}");
}
