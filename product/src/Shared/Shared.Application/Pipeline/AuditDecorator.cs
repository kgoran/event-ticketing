using EventTicketing.Shared.Application.Auditing;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.Application.Pipeline;

/// <summary>
/// Step 6: records who did what and why. It runs inside the transaction, so the audit entry
/// commits together with the change. Failed commands roll back and are not audited; they are
/// visible in logs, traces and metrics instead.
/// </summary>
internal sealed class AuditDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IAuditLog auditLog,
    IExecutionContext context,
    TimeProvider timeProvider)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        var result = await inner.HandleAsync(command, cancellationToken);

        if (result.IsSuccess)
        {
            var entry = new AuditEntry(
                typeof(TCommand).Name,
                context.UserId,
                (command as IHasAuditReason)?.Reason,
                timeProvider.GetUtcNow());

            await auditLog.RecordAsync(entry, cancellationToken);
        }

        return result;
    }
}
