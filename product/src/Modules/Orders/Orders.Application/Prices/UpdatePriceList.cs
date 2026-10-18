using EventTicketing.Catalog.Contracts;
using EventTicketing.Orders.Domain;
using EventTicketing.Shared.Application.Messaging;

namespace EventTicketing.Orders.Application.Prices;

/// <summary>Keeps Orders' own copy of each event's prices, so checkout never calls Catalog (ADR-0007).</summary>
internal sealed class UpdatePriceListOnEventPublished(IPriceList priceList) : IIntegrationEventHandler<EventPublished>
{
    public Task HandleAsync(EventPublished integrationEvent, CancellationToken cancellationToken) =>
        priceList.ApplyAsync(
            [.. integrationEvent.PriceCategories.Select(category => new PriceListEntry(
                integrationEvent.EventId, category.PriceCategoryId, category.PriceMinorUnits, integrationEvent.Currency, integrationEvent.OccurredAt))],
            cancellationToken);
}

/// <summary>
/// New prices apply to orders placed from now on; orders already placed keep theirs. The message may
/// overtake EventPublished on a retry, so the price list keeps whichever price Catalog set last.
/// </summary>
internal sealed class UpdatePriceListOnPricesChanged(IPriceList priceList) : IIntegrationEventHandler<PricesChanged>
{
    public Task HandleAsync(PricesChanged integrationEvent, CancellationToken cancellationToken) =>
        priceList.ApplyAsync(
            [.. integrationEvent.PriceCategories.Select(category => new PriceListEntry(
                integrationEvent.EventId, category.PriceCategoryId, category.PriceMinorUnits, integrationEvent.Currency, integrationEvent.OccurredAt))],
            cancellationToken);
}
