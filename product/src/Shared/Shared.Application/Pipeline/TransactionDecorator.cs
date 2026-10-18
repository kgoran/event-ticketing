using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Transactions;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.Application.Pipeline;

/// <summary>
/// Step 5: the handler, the audit entry and the outbox messages commit in one transaction of the
/// owning module, or not at all (ADR-0014).
/// </summary>
internal sealed class TransactionDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ITransactionRunner runner)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken) =>
        runner.RunAsync(typeof(TCommand), token => inner.HandleAsync(command, token), cancellationToken);
}
