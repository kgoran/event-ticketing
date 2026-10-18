using EventTicketing.Shared.Application.Auditing;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Shared.Infrastructure.Transactions;

namespace EventTicketing.Shared.Infrastructure.Auditing;

/// <summary>Writes audit entries into the open module transaction, so they commit together with the change they describe.</summary>
internal sealed class AuditLog(ModuleScope scope) : IAuditLog
{
    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        scope.Require("Recording an audit entry").Set<AuditRecord>().Add(new AuditRecord
        {
            Id = Guid.CreateVersion7(),
            Command = entry.Command,
            UserId = entry.UserId,
            Reason = entry.Reason,
            At = entry.At.ToUniversalTime(),
        });

        return Task.CompletedTask;
    }
}
