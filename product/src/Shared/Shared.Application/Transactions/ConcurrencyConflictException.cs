namespace EventTicketing.Shared.Application.Transactions;

/// <summary>
/// Another transaction changed the same row first (optimistic concurrency, ADR-0011). Nothing was
/// saved. Running the command again is safe: it loads the current state and decides again.
/// Message handlers are retried by the outbox relay or the scheduler; a coordinator may retry the
/// command itself.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
