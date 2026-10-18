using EventTicketing.Catalog.Contracts;
using EventTicketing.Catalog.Persistence;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Catalog.Events;

/// <summary>Publishes a Draft event, which tells Inventory and Orders it exists (EventPublished).</summary>
internal sealed record PublishEvent(Guid EventId) : ICommand<Unit>;

/// <summary>Only members of the event's organizer may publish it. An unknown event is refused, not reported as missing.</summary>
internal sealed class PublishEventAuthorizer(CatalogDbContext db) : ICommandAuthorizer<PublishEvent>
{
    public async ValueTask<bool> IsAllowedAsync(PublishEvent command, IExecutionContext context, CancellationToken cancellationToken)
    {
        var organizerId = await db.Events
            .Where(e => e.Id == command.EventId)
            .Select(e => (Guid?)e.OrganizerId)
            .SingleOrDefaultAsync(cancellationToken);

        return organizerId is { } id && context.OrganizerIds.Contains(id);
    }
}

internal sealed class PublishEventHandler(CatalogDbContext db, IIntegrationEventPublisher events, TimeProvider time)
    : ICommandHandler<PublishEvent, Unit>
{
    public async Task<Result<Unit>> HandleAsync(PublishEvent command, CancellationToken cancellationToken)
    {
        var @event = await db.Events
            .Include(e => e.PriceCategories)
            .Include(e => e.Seats)
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

        events.Publish(new EventPublished(
            Guid.CreateVersion7(),
            now,
            @event.Id,
            @event.OrganizerId,
            @event.Name,
            @event.StartsAt,
            @event.Currency,
            [.. @event.PriceCategories.Select(c => new PublishedPriceCategory(c.Id, c.Name, c.PriceMinorUnits))],
            [.. @event.Seats.Select(s => new PublishedSeat(s.Id, s.Section, s.Row, s.Number, s.PriceCategoryId))]));

        return Unit.Value;
    }
}
