using System.Text.Json;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Contracts;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Shared.Infrastructure.Transactions;

namespace EventTicketing.Shared.Infrastructure.Messaging;

/// <summary>
/// Adds the event to the outbox of the module whose transaction is open. The event's ID becomes
/// the message ID, which consumers use to drop duplicates.
/// </summary>
internal sealed class OutboxPublisher(ModuleScope scope) : IIntegrationEventPublisher
{
    public void Publish<TEvent>(TEvent integrationEvent)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var context = scope.Require("Publishing an integration event");
        var (name, version) = IntegrationEventMetadata.Of(typeof(TEvent));

        context.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = integrationEvent.EventId,
            Type = name,
            Version = version,
            Payload = JsonSerializer.Serialize(integrationEvent, MessagingJson.Options),
            Headers = MessageHeaders.FromCurrentActivity().ToJson(),
            OccurredAt = integrationEvent.OccurredAt.ToUniversalTime(),
        });
    }
}
