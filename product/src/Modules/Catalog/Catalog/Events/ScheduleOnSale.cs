using EventTicketing.Catalog.Contracts;
using EventTicketing.Catalog.Persistence;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Catalog.Events;

/// <summary>Sets when sales start; a time in the past opens sales now. Tells Inventory and the WaitingRoom once published.</summary>
internal sealed record ScheduleOnSale(Guid EventId, DateTimeOffset OnSaleAt) : ICommand<Unit>;

internal sealed class ScheduleOnSaleAuthorizer(CatalogDbContext db) : EventOrganizerAuthorizer<ScheduleOnSale>(db)
{
    protected override Guid EventIdOf(ScheduleOnSale command) => command.EventId;
}

internal sealed class ScheduleOnSaleHandler(CatalogDbContext db, IIntegrationEventPublisher events, TimeProvider time)
    : ICommandHandler<ScheduleOnSale, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ScheduleOnSale command, CancellationToken cancellationToken)
    {
        var @event = await db.Events.FindAsync([command.EventId], cancellationToken);
        if (@event is null)
        {
            return Error.NotFound("event.not_found", "The event does not exist.");
        }

        var now = time.GetUtcNow();
        var scheduled = @event.ScheduleOnSale(command.OnSaleAt, now);
        if (scheduled.IsFailure)
        {
            return scheduled.Error;
        }

        // A Draft event's schedule travels in EventPublished; nobody else knows the event yet.
        if (@event.Status == EventStatus.Published)
        {
            events.Publish(new OnSaleScheduled(Guid.CreateVersion7(), now, @event.Id, @event.OnSaleAt!.Value));
        }

        return Unit.Value;
    }
}
