using EventTicketing.Catalog.Contracts;
using EventTicketing.Inventory.Domain;
using EventTicketing.Shared.Application.Messaging;

namespace EventTicketing.Inventory.Application.Seats;

/// <summary>
/// Creates one sellable row per place when Catalog publishes an event: one per seat, and one
/// anonymous slot per standing place (ADR-0011). Also opens the event's sales window.
/// </summary>
internal sealed class CreatePlacesOnEventPublished(ISeatInventory seats, ISalesWindows windows) : IIntegrationEventHandler<EventPublished>
{
    public async Task HandleAsync(EventPublished integrationEvent, CancellationToken cancellationToken)
    {
        var seated = integrationEvent.Seats.Select(seat =>
            new NewPlace(seat.SeatId, seat.Section, seat.Row, seat.Number, seat.Rank, seat.PriceCategoryId));

        var standing = integrationEvent.StandingAreas.SelectMany(area =>
            Enumerable.Range(1, area.Capacity).Select(rank =>
                new NewPlace(Guid.CreateVersion7(), area.Section, null, null, rank, area.PriceCategoryId)));

        await seats.AddPlacesAsync(integrationEvent.EventId, [.. seated, .. standing], cancellationToken);

        var window = await windows.FindAsync(integrationEvent.EventId, cancellationToken);
        if (window is null)
        {
            window = SalesWindow.For(integrationEvent.EventId);
            windows.Add(window);
        }

        window.EndSalesAt(integrationEvent.StartsAt);
        window.ScheduleOnSale(integrationEvent.OnSaleAt, integrationEvent.OccurredAt);
    }
}
