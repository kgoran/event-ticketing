namespace EventTicketing.Shared.Application.Context;

/// <summary>Who is running the current command, and with which request metadata.</summary>
public interface IExecutionContext
{
    /// <summary>Subject of the caller's token; null for anonymous callers and system work.</summary>
    string? UserId { get; }

    bool IsAuthenticated { get; }

    IReadOnlySet<string> Roles { get; }

    /// <summary>The caller's email from their token, when it has one; null for anonymous callers and system work.</summary>
    string? Email { get; }

    /// <summary>Organizers the caller is a member of (ADR-0023).</summary>
    IReadOnlySet<Guid> OrganizerIds { get; }

    /// <summary>
    /// The Idempotency-Key header of an HTTP request, or the message ID of a consumed message
    /// (ADR-0033). Null when the caller sent none.
    /// </summary>
    string? IdempotencyKey { get; }
}
