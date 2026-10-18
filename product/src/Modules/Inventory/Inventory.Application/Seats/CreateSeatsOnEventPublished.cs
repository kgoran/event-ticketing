using EventTicketing.Catalog.Contracts;
using EventTicketing.Shared.Application.Messaging;

namespace EventTicketing.Inventory.Application.Seats;

/// <summary>Creates one sellable row per seat when Catalog publishes an event (ADR-0011).</summary>
internal sealed class CreateSeatsOnEventPublished(ISeatInventory seats) : IIntegrationEventHandler<EventPublished>
{
    public Task HandleAsync(EventPublished integrationEvent, CancellationToken cancellationToken) =>
        seats.AddSeatsAsync(
            integrationEvent.EventId,
            [.. integrationEvent.Seats.Select(seat => new NewSeat(seat.SeatId, seat.Section, seat.Row, seat.Number, seat.PriceCategoryId))],
            cancellationToken);
}
