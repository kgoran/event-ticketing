using EventTicketing.Catalog.Contracts;
using EventTicketing.Orders.Domain;
using EventTicketing.Shared.Application.Messaging;

namespace EventTicketing.Orders.Application.Prices;

/// <summary>Keeps Orders' own copy of each event's prices, so checkout never calls Catalog (ADR-0007).</summary>
internal sealed class UpdatePriceListOnEventPublished(IPriceList priceList) : IIntegrationEventHandler<EventPublished>
{
    public Task HandleAsync(EventPublished integrationEvent, CancellationToken cancellationToken)
    {
        priceList.Add(integrationEvent.PriceCategories.Select(category =>
            new PriceListEntry(integrationEvent.EventId, category.PriceCategoryId, category.PriceMinorUnits, integrationEvent.Currency)));

        return Task.CompletedTask;
    }
}
