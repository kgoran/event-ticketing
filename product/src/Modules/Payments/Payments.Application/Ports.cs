using EventTicketing.Payments.Domain;

namespace EventTicketing.Payments.Application;

/// <summary>
/// A payment provider behind one interface (ADR-0004, docs/payments.md): Stripe and LocalPay. Each
/// adapter translates the provider's model into ours.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    /// <summary>Creates a payment session. The order ID is the idempotency key, so a retry returns the same session.</summary>
    Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Makes sure no payment can complete any more (Stripe: expire the Checkout Session; ADR-0010).
    /// Cancelled also when nothing can be paid anyway; AlreadyCompleted when the payment went through.
    /// </summary>
    Task<CancelPaymentResult> CancelPaymentAsync(string providerPaymentId, CancellationToken cancellationToken);

    /// <summary>Asks the provider where a payment stands, e.g. when its webhook is late or missing.</summary>
    Task<ProviderPaymentStatus> GetStatusAsync(string providerPaymentId, CancellationToken cancellationToken);

    /// <summary>Payments created in a period, for reconciliation (M4): finds payments that have no order.</summary>
    IAsyncEnumerable<ProviderPayment> ListPaymentsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>
    /// Refunds (part of) a succeeded payment. Our refund ID is the idempotency key, so asking again
    /// returns the same refund. Pending means the provider reports the outcome by webhook.
    /// </summary>
    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken);

    /// <summary>Checks the webhook's signature and reads it; null if the signature is missing, wrong or too old.</summary>
    ProviderWebhook? VerifyWebhook(string payload, IReadOnlyDictionary<string, string> headers);
}

public sealed record RefundRequest(Guid RefundId, string ProviderPaymentId, long AmountMinorUnits, string Currency, string Reason);

public enum ProviderRefundStatus
{
    Pending,
    Succeeded,
    Failed,
}

public sealed record RefundResult(ProviderRefundStatus Status, string? ProviderRefundId, string? FailureReason = null);

public sealed record CreatePaymentRequest(Guid OrderId, long AmountMinorUnits, string Currency, string ReturnUrl, DateTimeOffset ExpiresAt);

public sealed record CreatePaymentResult(string ProviderPaymentId, string CheckoutUrl);

public enum CancelPaymentResult
{
    Cancelled,
    AlreadyCompleted,
}

public enum ProviderPaymentStatus
{
    /// <summary>The fan can still pay.</summary>
    Open,
    Succeeded,
    Failed,

    /// <summary>The session expired or was cancelled: nobody can pay it any more.</summary>
    Expired,
}

public sealed record ProviderPayment(string ProviderPaymentId, string Reference, long AmountMinorUnits, string Currency, ProviderPaymentStatus Status);

/// <param name="Reference">Our reference at the provider: the order ID.</param>
/// <param name="AmountMinorUnits">The payment's amount, or for a refund webhook the refund's.</param>
/// <param name="ProviderRefundId">Set for refund webhooks.</param>
public sealed record ProviderWebhook(
    string EventId,
    string Type,
    string ProviderPaymentId,
    string Reference,
    long AmountMinorUnits,
    string Currency,
    string? ProviderRefundId = null);

public static class ProviderWebhookTypes
{
    public const string PaymentSucceeded = "payment.succeeded";
    public const string PaymentFailed = "payment.failed";
    public const string RefundSucceeded = "refund.succeeded";
    public const string RefundFailed = "refund.failed";
}

public interface IPaymentAttemptRepository
{
    void Add(PaymentAttempt attempt);

    Task<PaymentAttempt?> FindAsync(Guid attemptId, CancellationToken cancellationToken);

    Task<PaymentAttempt?> FindByOrderAsync(Guid orderId, CancellationToken cancellationToken);

    Task<PaymentAttempt?> FindByProviderPaymentAsync(string provider, string providerPaymentId, CancellationToken cancellationToken);
}

public interface IRefundRepository
{
    void Add(Refund refund);

    Task<Refund?> FindAsync(Guid refundId, CancellationToken cancellationToken);

    Task<Refund?> FindByAttemptAsync(Guid attemptId, CancellationToken cancellationToken);

    Task<Refund?> FindByProviderRefundAsync(string providerRefundId, CancellationToken cancellationToken);
}

public interface IWebhookRepository
{
    void Add(ReceivedWebhook webhook);

    Task<bool> ExistsAsync(string provider, string providerEventId, CancellationToken cancellationToken);

    Task<ReceivedWebhook?> FindAsync(Guid webhookId, CancellationToken cancellationToken);
}

/// <summary>Configuration section "Payments".</summary>
public sealed record PaymentsPolicy
{
    /// <summary>The provider new checkouts use.</summary>
    public string Provider { get; init; } = "localpay";

    /// <summary>The provider session's lifetime: Stripe's minimum is 30 minutes (ADR-0010).</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(30);
}
