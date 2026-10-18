using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.Application.Commands;

/// <summary>A request to change state. <typeparamref name="TResult"/> is the value returned on success.</summary>
public interface ICommand<TResult>
{
}

/// <summary>
/// Handles one command type. Handlers contain only the use case; the cross-cutting steps
/// (context, authorization, validation, idempotency, transaction, audit, tracing) are decorators
/// wrapped around every handler (ADR-0033).
/// </summary>
/// <summary>
/// A command that coordinates other commands (its own module's, and other modules' through their
/// module APIs) and may call external services, so it must not hold a database transaction open
/// (ADR-0009, ADR-0033). The pipeline runs it without the transaction and audit steps; the commands
/// it calls run with their own.
/// </summary>
public interface INonTransactionalCommand
{
}

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
