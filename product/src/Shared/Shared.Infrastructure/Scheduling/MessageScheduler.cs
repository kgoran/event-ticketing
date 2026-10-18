using System.Text.Json;
using EventTicketing.Shared.Application.Scheduling;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Shared.Infrastructure.Transactions;

namespace EventTicketing.Shared.Infrastructure.Scheduling;

/// <summary>
/// Adds a scheduled message to the module whose transaction is open. The message is stored under
/// its .NET type name, so renaming a message type strands messages already scheduled with the old name.
/// </summary>
internal sealed class MessageScheduler(ModuleScope scope) : IMessageScheduler
{
    public void Schedule<TMessage>(TMessage message, DateTimeOffset dueAt)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);

        scope.Require("Scheduling a message").Set<ScheduledMessage>().Add(new ScheduledMessage
        {
            Id = Guid.CreateVersion7(),
            Type = typeof(TMessage).FullName!,
            Payload = JsonSerializer.Serialize(message, MessagingJson.Options),
            Headers = MessageHeaders.FromCurrentActivity().ToJson(),
            DueAt = dueAt.ToUniversalTime(),
        });
    }
}
