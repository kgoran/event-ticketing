using EventTicketing.Catalog.Contracts;
using EventTicketing.CheckIn.Domain;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Ticketing.Contracts;

namespace EventTicketing.CheckIn.Application.DoorLists;

/// <summary>CheckIn keeps its own copy of a published event (ADR-0007): its organizer manages the devices.</summary>
internal sealed class RememberEventOnEventPublished(ICheckInEvents events) : IIntegrationEventHandler<EventPublished>
{
    public Task HandleAsync(EventPublished integrationEvent, CancellationToken cancellationToken) =>
        events.AddAsync(new CheckInEvent(integrationEvent.EventId, integrationEvent.OrganizerId, integrationEvent.Name, integrationEvent.StartsAt), cancellationToken);
}

/// <summary>Issued tickets join the door list (ADR-0013): IDs only, no personal data.</summary>
internal sealed class AddToDoorListOnTicketsIssued(IDoorList doorList, TimeProvider time) : IIntegrationEventHandler<TicketsIssued>
{
    public Task HandleAsync(TicketsIssued integrationEvent, CancellationToken cancellationToken) =>
        doorList.AddAsync(integrationEvent.EventId, integrationEvent.Tickets.Select(ticket => ticket.TicketId), DoorListChange.Issued, time.GetUtcNow(), cancellationToken);
}

/// <summary>Revoked tickets go on the door list's revocations (D63): devices refuse them once they have synced.</summary>
internal sealed class RevokeOnTicketsRevoked(IDoorList doorList, TimeProvider time) : IIntegrationEventHandler<TicketsRevoked>
{
    public Task HandleAsync(TicketsRevoked integrationEvent, CancellationToken cancellationToken) =>
        doorList.AddAsync(integrationEvent.EventId, integrationEvent.TicketIds, DoorListChange.Revoked, time.GetUtcNow(), cancellationToken);
}
