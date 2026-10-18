using EventTicketing.Catalog.Contracts;
using EventTicketing.Catalog.Persistence;
using EventTicketing.Catalog.Venues;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Catalog.Events;

/// <summary>
/// Publishes a Draft event. EventPublished carries every place with its price category, so
/// Inventory creates its rows and Orders its price list without calling Catalog (ADR-0007).
/// </summary>
internal sealed record PublishEvent(Guid EventId) : ICommand<Unit>;

internal sealed class PublishEventAuthorizer(CatalogDbContext db) : EventOrganizerAuthorizer<PublishEvent>(db)
{
    protected override Guid EventIdOf(PublishEvent command) => command.EventId;
}

internal sealed class PublishEventHandler(CatalogDbContext db, IIntegrationEventPublisher events, TimeProvider time)
    : ICommandHandler<PublishEvent, Unit>
{
    public async Task<Result<Unit>> HandleAsync(PublishEvent command, CancellationToken cancellationToken)
    {
        var @event = await db.Events
            .Include(e => e.PriceCategories)
            .Include(e => e.PriceAssignments)
            .AsSplitQuery()
            .SingleOrDefaultAsync(e => e.Id == command.EventId, cancellationToken);

        if (@event is null)
        {
            return Error.NotFound("event.not_found", "The event does not exist.");
        }

        var now = time.GetUtcNow();
        var published = @event.Publish(now);
        if (published.IsFailure)
        {
            return published.Error;
        }

        var venue = await db.Venues
            .AsNoTracking()
            .Include(v => v.Sections).ThenInclude(s => s.Seats)
            .AsSplitQuery()
            .SingleAsync(v => v.Id == @event.VenueId, cancellationToken);

        var sections = venue.Sections.OrderBy(s => s.Position).ToList();

        events.Publish(new EventPublished(
            Guid.CreateVersion7(),
            now,
            @event.Id,
            @event.OrganizerId,
            @event.Name,
            @event.StartsAt,
            venue.TimeZone,
            @event.Currency,
            @event.OnSaleAt,
            [.. @event.PriceCategories.Select(c => new PublishedPriceCategory(c.Id, c.Name, c.PriceMinorUnits))],
            [.. sections
                .Where(s => s.Kind == SectionKind.Seated)
                .SelectMany(s => s.Seats.OrderBy(seat => seat.Rank).Select(seat => new PublishedSeat(
                    seat.Id, s.Code, seat.Row, seat.Number, seat.Rank, @event.PriceCategoryIdFor(s.Code, seat.Row)!.Value)))],
            [.. sections
                .Where(s => s.Kind == SectionKind.Standing)
                .Select(s => new PublishedStandingArea(s.Code, s.Capacity!.Value, @event.PriceCategoryIdFor(s.Code, null)!.Value))]));

        return Unit.Value;
    }
}
