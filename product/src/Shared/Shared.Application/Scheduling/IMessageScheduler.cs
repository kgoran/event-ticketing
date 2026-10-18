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

/// <summary>
/// Marks a scheduled message handler that coordinates, like an <c>INonTransactionalCommand</c>: it
/// calls other modules or external services, so it runs outside any transaction and every step it
/// takes is a command with its own. The scheduled row is deleted only after the handler finishes,
/// so a crash in between runs it again: each of its steps must be safe to repeat.
/// </summary>
public interface INonTransactionalMessageHandler
{
}
