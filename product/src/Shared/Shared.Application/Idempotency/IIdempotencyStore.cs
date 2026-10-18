using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.Application.Idempotency;

/// <summary>
/// Remembers the outcome of each idempotency key, so a repeated request returns the first
/// outcome instead of running again. A claim is a lease: if the process dies before completing
/// it, the claim expires and the key can be claimed again.
/// </summary>
public interface IIdempotencyStore
{
    Task<IdempotencyClaim> TryClaimAsync(string key, string requestName, CancellationToken cancellationToken);

    Task CompleteAsync(string key, string requestName, StoredResult result, CancellationToken cancellationToken);

    /// <summary>Gives the key back after an exception, so the client can retry with it.</summary>
    Task ReleaseAsync(string key, string requestName, CancellationToken cancellationToken);
}

public abstract record IdempotencyClaim
{
    private IdempotencyClaim()
    {
    }

    /// <summary>This request owns the key and runs.</summary>
    public sealed record Claimed : IdempotencyClaim;

    /// <summary>Another request with the same key is still running.</summary>
    public sealed record InProgress : IdempotencyClaim;

    /// <summary>The key was used before; replay its outcome.</summary>
    public sealed record AlreadyCompleted(StoredResult Result) : IdempotencyClaim;
}

/// <summary>A command's outcome as stored for replay: the success value as JSON, or the error.</summary>
public sealed record StoredResult(
    bool IsSuccess,
    string? ValueJson,
    string? ErrorCode,
    string? ErrorMessage,
    ErrorType? ErrorType);
