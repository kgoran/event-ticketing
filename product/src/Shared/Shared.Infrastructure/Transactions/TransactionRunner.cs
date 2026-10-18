using EventTicketing.Shared.Application.Transactions;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Shared.Infrastructure.Transactions;

/// <summary>
/// One database transaction per command or consumed message, in the module that owns it
/// (ADR-0009, ADR-0014). The handler's changes, the events it publishes, the messages it schedules
/// and its audit entry are saved together and committed once, or not at all.
/// </summary>
internal sealed class TransactionRunner(IServiceProvider services, ModuleRegistry modules, ModuleScope scope)
    : ITransactionRunner
{
    public async Task<Result<TResult>> RunAsync<TResult>(
        Type commandType,
        Func<CancellationToken, Task<Result<TResult>>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var module = modules.Resolve(commandType);

        // A command of the same module called from inside another one joins its transaction.
        if (scope.CurrentModule == module)
        {
            return await work(cancellationToken);
        }

        // A different module gets its own transaction: it commits independently, by design (ADR-0009).
        // Coming back into a module that is already open further out would mix two transactions.
        if (scope.IsOpen(module))
        {
            throw new InvalidOperationException(
                $"{commandType.Name} would re-enter module '{module.Schema}', which already has a transaction open further out.");
        }

        var context = (ModuleDbContext)services.GetRequiredService(module.ContextType);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        using (scope.Enter(module, context))
        {
            try
            {
                var result = await work(cancellationToken);

                if (result.IsFailure)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    context.ChangeTracker.Clear();
                    return result;
                }

                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                // The next command in this scope must load current state, not this command's copies.
                context.ChangeTracker.Clear();
                return result;
            }
            catch (DbUpdateConcurrencyException exception)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                context.ChangeTracker.Clear();
                throw new ConcurrencyConflictException($"{commandType.Name} lost a race: another transaction changed the same data first.", exception);
            }
            catch (DbUpdateException exception) when (context.ErrorFor(exception) is { } error)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                context.ChangeTracker.Clear();
                return Result.Failure<TResult>(error);
            }
            catch
            {
                context.ChangeTracker.Clear();
                throw;
            }
        }
    }
}
