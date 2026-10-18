using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.Application.Transactions;

/// <summary>
/// Runs a command's work in one database transaction of the module that owns the command,
/// together with the outbox messages it writes (ADR-0014). Commits only when the work succeeds.
/// </summary>
public interface ITransactionRunner
{
    Task<Result<TResult>> RunAsync<TResult>(
        Type commandType,
        Func<CancellationToken, Task<Result<TResult>>> work,
        CancellationToken cancellationToken);
}
