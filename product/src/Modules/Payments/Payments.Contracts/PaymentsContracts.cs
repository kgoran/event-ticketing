using EventTicketing.Shared.Contracts;

namespace EventTicketing.Payments.Contracts;

/// <summary>
/// Payments' module API (ADR-0007). Only Orders calls it, to coordinate checkout (ADR-0009).
/// Designed as if it were remote: asynchronous, plain data in and out, every call safe to repeat.
/// </summary>
public interface IPaymentsApi
{
    /// <summary>
    /// Creates (or returns the existing) payment session for the order at the provider and returns
    /// the URL the fan pays on. Safe to repeat: the order ID is the idempotency key.
    /// </summary>
    Task<CreateCheckoutResult> CreateCheckoutAsync(CreateCheckoutRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// At the checkout deadline: makes sure the order's payment can no longer complete (ADR-0010).
    /// Cancelled means the seats can be released; AlreadyCompleted means wait for PaymentSucceeded.
    /// </summary>
    Task<CancelCheckoutResult> CancelCheckoutAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider about the order's payment, for when its webhook is late or missing. A
    /// success found this way publishes PaymentSucceeded, exactly as the webhook would have.
    /// </summary>
    Task<PaymentStatusResult> CheckPaymentStatusAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Refunds the order's payment in full, e.g. when its seats were lost (ADR-0009). Safe to repeat:
    /// one refund per payment. RefundSucceeded or RefundFailed follows.
    /// </summary>
    Task<RefundRequestResult> RequestRefundAsync(Guid orderId, string reason, CancellationToken cancellationToken);
}

public enum RefundRequestOutcome
{
    /// <summary>The refund is recorded and the provider asked; RefundSucceeded or RefundFailed follows.</summary>
    Requested,

    /// <summary>The order's payment didn't succeed: there's nothing to refund.</summary>
    NotRefundable,

    /// <summary>The provider couldn't be reached; try again.</summary>
    ProviderUnavailable,
}

public sealed record RefundRequestResult(RefundRequestOutcome Outcome);

public sealed record CreateCheckoutRequest(Guid OrderId, long AmountMinorUnits, string Currency, string ReturnUrl);

public enum CreateCheckoutOutcome
{
    Created,
    ProviderUnavailable,
}

public sealed record CreateCheckoutResult(CreateCheckoutOutcome Outcome, string? RedirectUrl);

public enum CancelCheckoutOutcome
{
    Cancelled,
    AlreadyCompleted,

    /// <summary>The provider couldn't be reached; try again (the seats stay held until the backstop at most).</summary>
    ProviderUnavailable,
}

public sealed record CancelCheckoutResult(CancelCheckoutOutcome Outcome);

public enum PaymentStatusOutcome
{
    /// <summary>The fan can still pay.</summary>
    Pending,
    Succeeded,
    Failed,

    /// <summary>Expired, cancelled, or never created: nothing can be paid.</summary>
    Cancelled,
    ProviderUnavailable,
}

public sealed record PaymentStatusResult(PaymentStatusOutcome Outcome);

/// <summary>The provider confirmed a payment (via its signed webhook, ADR-0009). The webhook is the source of truth, not the redirect.</summary>
[IntegrationEvent("payments.payment-succeeded", 1)]
public sealed record PaymentSucceeded(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    Guid PaymentAttemptId,
    long AmountMinorUnits,
    string Currency) : IIntegrationEvent;

/// <summary>An order's payment was refunded at the provider.</summary>
[IntegrationEvent("payments.refund-succeeded", 1)]
public sealed record RefundSucceeded(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    Guid RefundId,
    long AmountMinorUnits,
    string Currency) : IIntegrationEvent;

/// <summary>The provider refused or failed a refund. A person has to look at it.</summary>
[IntegrationEvent("payments.refund-failed", 1)]
public sealed record RefundFailed(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    Guid RefundId,
    string Reason) : IIntegrationEvent;

/// <summary>The provider reported the payment failed, e.g. the card was declined. Nothing was charged.</summary>
[IntegrationEvent("payments.payment-failed", 1)]
public sealed record PaymentFailed(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    Guid PaymentAttemptId) : IIntegrationEvent;
