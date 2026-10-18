using EventTicketing.Shared.Contracts;

namespace EventTicketing.Payments.Contracts;

/// <summary>
/// Payments' module API (ADR-0007). Only Orders calls it, to coordinate checkout (ADR-0009).
/// Designed as if it were remote: asynchronous, plain data in and out.
/// </summary>
public interface IPaymentsApi
{
    /// <summary>
    /// Creates (or returns the existing) payment session for the order at the provider and returns
    /// the URL the fan pays on. Safe to repeat: the order ID is the idempotency key.
    /// </summary>
    Task<CreateCheckoutResult> CreateCheckoutAsync(CreateCheckoutRequest request, CancellationToken cancellationToken);
}

public sealed record CreateCheckoutRequest(Guid OrderId, long AmountMinorUnits, string Currency, string ReturnUrl);

public enum CreateCheckoutOutcome
{
    Created,
    ProviderUnavailable,
}

public sealed record CreateCheckoutResult(CreateCheckoutOutcome Outcome, string? RedirectUrl);

/// <summary>The provider confirmed a payment (via its signed webhook, ADR-0009). The webhook is the source of truth, not the redirect.</summary>
[IntegrationEvent("payments.payment-succeeded", 1)]
public sealed record PaymentSucceeded(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    Guid PaymentAttemptId,
    long AmountMinorUnits,
    string Currency) : IIntegrationEvent;
