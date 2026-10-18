namespace EventTicketing.Shared.Application.Scheduling;

/// <summary>
/// Schedules a module-internal message for later, e.g. a checkout deadline (ADR-0010). Saved in
/// the current transaction, so it exists only if the command commits. Only valid inside a
/// command's or consumer's transaction.
/// </summary>
public interface IMessageScheduler
{
    void Schedule<TMessage>(TMessage message, DateTimeOffset dueAt)
        where TMessage : class;
}

/// <summary>
/// Handles a scheduled message when it is due, in the module's transaction. The scheduled row is
/// deleted in that same transaction, so a handler's work and "done" commit together.
/// </summary>
public interface IScheduledMessageHandler<in TMessage>
    where TMessage : class
{
    Task HandleAsync(TMessage message, CancellationToken cancellationToken);
}
