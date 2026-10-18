using EventTicketing.Payments.Domain;

namespace EventTicketing.Payments.Application;

/// <summary>
/// A payment provider behind one interface (ADR-0004): Stripe and LocalPay. Each adapter translates
/// the provider's model into ours. Grows with backlog items P1/P2 (cancel, status, list, refund).
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    /// <summary>Creates a payment session. The order ID is the idempotency key, so a retry returns the same session.</summary>
    Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);

    /// <summary>Checks the webhook's signature and reads it; null if the signature is missing, wrong or too old.</summary>
    ProviderWebhook? VerifyWebhook(string payload, IReadOnlyDictionary<string, string> headers);
}

public sealed record CreatePaymentRequest(Guid OrderId, long AmountMinorUnits, string Currency, string ReturnUrl, DateTimeOffset ExpiresAt);

public sealed record CreatePaymentResult(string ProviderPaymentId, string CheckoutUrl);

public sealed record ProviderWebhook(string EventId, string Type, string ProviderPaymentId);

public static class ProviderWebhookTypes
{
    public const string PaymentSucceeded = "payment.succeeded";
    public const string PaymentFailed = "payment.failed";
}

public interface IPaymentAttemptRepository
{
    void Add(PaymentAttempt attempt);

    Task<PaymentAttempt?> FindAsync(Guid attemptId, CancellationToken cancellationToken);

    Task<PaymentAttempt?> FindByOrderAsync(Guid orderId, CancellationToken cancellationToken);

    Task<PaymentAttempt?> FindByProviderPaymentAsync(string provider, string providerPaymentId, CancellationToken cancellationToken);
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
