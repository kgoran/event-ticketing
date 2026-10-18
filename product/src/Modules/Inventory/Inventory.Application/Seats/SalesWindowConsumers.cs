using EventTicketing.Catalog.Contracts;
using EventTicketing.Inventory.Domain;
using EventTicketing.Shared.Application.Messaging;

namespace EventTicketing.Inventory.Application.Seats;

/// <summary>Keeps Inventory's copy of the on-sale time when the organizer sets or moves it.</summary>
internal sealed class UpdateSalesWindowOnOnSaleScheduled(ISalesWindows windows) : IIntegrationEventHandler<OnSaleScheduled>
{
    public async Task HandleAsync(OnSaleScheduled integrationEvent, CancellationToken cancellationToken)
    {
        // Messages from one outbox can overtake each other when a delivery is retried, so this may
        // arrive before EventPublished: the window is created here and completed later.
        var window = await windows.FindAsync(integrationEvent.EventId, cancellationToken);
        if (window is null)
        {
            window = SalesWindow.For(integrationEvent.EventId);
            windows.Add(window);
        }

        window.ScheduleOnSale(integrationEvent.OnSaleAt, integrationEvent.OccurredAt);
    }
}
