namespace EventTicketing.Payments.Domain;

public enum PaymentStatus
{
    Created,
    AwaitingPayment,
    Succeeded,
    Failed,

    /// <summary>Cancelled at the provider before anything was paid (ADR-0010).</summary>
    Cancelled,
}

/// <summary>
/// One order's payment at one provider:
/// <code>
///   Created ──session stored──► AwaitingPayment ──► Succeeded | Failed | Cancelled
/// </code>
/// News from the provider can arrive in any order (webhooks, status checks): a success is never
/// overridden, and storing the session never undoes an outcome that arrived first.
/// </summary>
public sealed class PaymentAttempt
{
    private PaymentAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public long AmountMinorUnits { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public string Provider { get; private set; } = string.Empty;

    public string? ProviderPaymentId { get; private set; }

    public string? CheckoutUrl { get; private set; }

    public PaymentStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static PaymentAttempt Create(Guid orderId, long amountMinorUnits, string currency, string provider, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderId = orderId,
        AmountMinorUnits = amountMinorUnits,
        Currency = currency,
        Provider = provider,
        Status = PaymentStatus.Created,
        CreatedAt = now.ToUniversalTime(),
    };

    public void SessionCreated(string providerPaymentId, string checkoutUrl)
    {
        ProviderPaymentId = providerPaymentId;
        CheckoutUrl = checkoutUrl;

        // The webhook can overtake the call that stores the session; its outcome stands.
        if (Status == PaymentStatus.Created)
        {
            Status = PaymentStatus.AwaitingPayment;
        }
    }

    /// <summary>Whether the provider's report is about this payment: same amount and currency.</summary>
    public bool Matches(long amountMinorUnits, string currency) =>
        amountMinorUnits == AmountMinorUnits && string.Equals(currency, Currency, StringComparison.OrdinalIgnoreCase);

    /// <summary>Marks the payment succeeded. Returns false if it already was (a repeated webhook or status check).</summary>
    public bool Succeed(DateTimeOffset now)
    {
        if (Status == PaymentStatus.Succeeded)
        {
            return false;
        }

        Status = PaymentStatus.Succeeded;
        CompletedAt = now.ToUniversalTime();
        return true;
    }

    /// <summary>Marks the payment failed. Returns false if nothing changed: already failed, or a late failure after a success.</summary>
    public bool Fail(DateTimeOffset now)
    {
        if (Status is PaymentStatus.Succeeded or PaymentStatus.Failed)
        {
            return false;
        }

        Status = PaymentStatus.Failed;
        CompletedAt = now.ToUniversalTime();
        return true;
    }

    /// <summary>Records that the provider cancelled the session. A success that arrived first stands.</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status is PaymentStatus.Succeeded or PaymentStatus.Failed or PaymentStatus.Cancelled)
        {
            return;
        }

        Status = PaymentStatus.Cancelled;
        CompletedAt = now.ToUniversalTime();
    }
}

/// <summary>A verified webhook from a provider, stored before it is processed (the payments inbox, ADR-0009).</summary>
public sealed class ReceivedWebhook
{
    private ReceivedWebhook()
    {
    }

    public Guid Id { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public string ProviderEventId { get; private set; } = string.Empty;

    public string Type { get; private set; } = string.Empty;

    public string ProviderPaymentId { get; private set; } = string.Empty;

    /// <summary>Our reference at the provider: the order ID.</summary>
    public string Reference { get; private set; } = string.Empty;

    public long AmountMinorUnits { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>Set for refund webhooks.</summary>
    public string? ProviderRefundId { get; private set; }

    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public static ReceivedWebhook Receive(
        string provider,
        string providerEventId,
        string type,
        string providerPaymentId,
        string reference,
        long amountMinorUnits,
        string currency,
        string? providerRefundId,
        string payload,
        DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Provider = provider,
        ProviderEventId = providerEventId,
        Type = type,
        ProviderPaymentId = providerPaymentId,
        Reference = reference,
        AmountMinorUnits = amountMinorUnits,
        Currency = currency,
        ProviderRefundId = providerRefundId,
        Payload = payload,
        ReceivedAt = now.ToUniversalTime(),
    };

    public void MarkProcessed(DateTimeOffset now) => ProcessedAt = now.ToUniversalTime();
}
