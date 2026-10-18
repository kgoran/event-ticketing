using EventTicketing.Catalog.Contracts;
using EventTicketing.Catalog.Persistence;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Catalog.Events;

public sealed record PriceChange(Guid PriceCategoryId, long PriceMinorUnits);

/// <summary>Changes the price of some categories. Orders already placed keep their price.</summary>
internal sealed record ChangePrices(Guid EventId, IReadOnlyList<PriceChange> Prices) : ICommand<Unit>;

internal sealed class ChangePricesValidator : AbstractValidator<ChangePrices>
{
    public ChangePricesValidator()
    {
        RuleFor(command => command.Prices).NotEmpty();
        RuleForEach(command => command.Prices).ChildRules(change => change.RuleFor(c => c.PriceMinorUnits).GreaterThanOrEqualTo(0));
        RuleFor(command => command.Prices)
            .Must(prices => prices.Select(p => p.PriceCategoryId).Distinct().Count() == prices.Count)
            .WithMessage("Each price category can appear only once.");
    }
}

internal sealed class ChangePricesAuthorizer(CatalogDbContext db) : EventOrganizerAuthorizer<ChangePrices>(db)
{
    protected override Guid EventIdOf(ChangePrices command) => command.EventId;
}

internal sealed class ChangePricesHandler(CatalogDbContext db, IIntegrationEventPublisher events, TimeProvider time)
    : ICommandHandler<ChangePrices, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ChangePrices command, CancellationToken cancellationToken)
    {
        var @event = await db.Events
            .Include(e => e.PriceCategories)
            .SingleOrDefaultAsync(e => e.Id == command.EventId, cancellationToken);

        if (@event is null)
        {
            return Error.NotFound("event.not_found", "The event does not exist.");
        }

        var now = time.GetUtcNow();
        var changed = @event.ChangePrices(command.Prices, now);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        // The full price list, not only the changes: a consumer can apply it without knowing the old one.
        if (@event.Status == EventStatus.Published)
        {
            events.Publish(new PricesChanged(
                Guid.CreateVersion7(),
                now,
                @event.Id,
                @event.Currency,
                [.. @event.PriceCategories.Select(c => new PublishedPriceCategory(c.Id, c.Name, c.PriceMinorUnits))]));
        }

        return Unit.Value;
    }
}
