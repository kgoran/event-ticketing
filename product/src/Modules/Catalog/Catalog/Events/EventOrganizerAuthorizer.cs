using EventTicketing.Catalog.Persistence;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Context;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Catalog.Events;

/// <summary>
/// Allows members of the organizer that owns the event the command is about (ADR-0023). An unknown
/// event is refused, not reported as missing, so IDs of other organizers' events reveal nothing.
/// Derive one sealed class per command.
/// </summary>
internal abstract class EventOrganizerAuthorizer<TCommand>(CatalogDbContext db) : ICommandAuthorizer<TCommand>
{
    public async ValueTask<bool> IsAllowedAsync(TCommand command, IExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var eventId = EventIdOf(command);
        var organizerId = await db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (Guid?)e.OrganizerId)
            .SingleOrDefaultAsync(cancellationToken);

        return context.IsAuthenticated && organizerId is { } id && context.OrganizerIds.Contains(id);
    }

    protected abstract Guid EventIdOf(TCommand command);
}
