namespace EventTicketing.Payments.Domain;

public enum RefundStatus
{
    /// <summary>Recorded; the provider has been or is about to be asked.</summary>
    Requested,
    Succeeded,
    Failed,
}

/// <summary>
/// Money going back for one order's payment. M1a has a single full refund per payment, for the
/// "seats lost" case (ADR-0009); mass refunds come with event cancellation (M4).
/// <code>
///   Requested ──provider or webhook──► Succeeded | Failed
/// </code>
/// </summary>
public sealed class Refund
{
    private Refund()
    {
    }

    public Guid Id { get; private set; }

    public Guid PaymentAttemptId { get; private set; }

    public Guid OrderId { get; private set; }

    public long AmountMinorUnits { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public RefundStatus Status { get; private set; }

    public string? ProviderRefundId { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>A full refund of a succeeded payment.</summary>
    public static Refund For(PaymentAttempt attempt, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        return new Refund
        {
            Id = Guid.CreateVersion7(),
            PaymentAttemptId = attempt.Id,
            OrderId = attempt.OrderId,
            AmountMinorUnits = attempt.AmountMinorUnits,
            Currency = attempt.Currency,
            Reason = reason,
            Status = RefundStatus.Requested,
            RequestedAt = now.ToUniversalTime(),
        };
    }

    /// <summary>Returns false if it already succeeded (a repeated report).</summary>
    public bool Succeed(string? providerRefundId, DateTimeOffset now)
    {
        ProviderRefundId ??= providerRefundId;
        if (Status == RefundStatus.Succeeded)
        {
            return false;
        }

        Status = RefundStatus.Succeeded;
        CompletedAt = now.ToUniversalTime();
        return true;
    }

    /// <summary>Returns false if nothing changed: already failed, or a late failure after a success.</summary>
    public bool Fail(string reason, DateTimeOffset now)
    {
        if (Status != RefundStatus.Requested)
        {
            return false;
        }

        Status = RefundStatus.Failed;
        FailureReason = reason;
        CompletedAt = now.ToUniversalTime();
        return true;
    }
}
