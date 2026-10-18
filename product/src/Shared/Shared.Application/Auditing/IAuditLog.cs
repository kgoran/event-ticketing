namespace EventTicketing.Shared.Application.Auditing;

/// <summary>Who did what, when, and why: recorded per successful command, not per row (ADR-0033).</summary>
public sealed record AuditEntry(string Command, string? UserId, string? Reason, DateTimeOffset At);

public interface IAuditLog
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>A command that carries a human reason, e.g. a manual refund.</summary>
public interface IHasAuditReason
{
    string? Reason { get; }
}
