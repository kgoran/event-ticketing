using EventTicketing.Shared.Contracts;

namespace EventTicketing.Shared.Application.Messaging;

/// <summary>
/// Publishes an integration event through the outbox (ADR-0014): the event is saved in the same
/// transaction as the command's changes and delivered after the commit, so it is never lost and
/// never sent for a change that was rolled back. Only valid inside a command's or consumer's
/// transaction.
/// </summary>
public interface IIntegrationEventPublisher
{
    void Publish<TEvent>(TEvent integrationEvent)
        where TEvent : IIntegrationEvent;
}

/// <summary>
/// Reacts to an integration event from another module (or the same one). Runs in the consuming
/// module's own transaction. Delivery is at least once; the inbox drops duplicates before the
/// handler runs, but handlers must still tolerate events arriving out of order.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
