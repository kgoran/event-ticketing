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
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
