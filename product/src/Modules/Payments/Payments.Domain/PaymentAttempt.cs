namespace EventTicketing.Payments.Domain;

public enum PaymentStatus
{
    Created,
    AwaitingPayment,
    Succeeded,
    Failed,
}

/// <summary>One order's payment at one provider: Created → AwaitingPayment → Succeeded or Failed.</summary>
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
        Status = PaymentStatus.AwaitingPayment;
    }

    /// <summary>Marks the payment succeeded. Returns false if it already was (a repeated webhook).</summary>
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

    public void Fail(DateTimeOffset now)
    {
        if (Status == PaymentStatus.Succeeded)
        {
            return; // a late failure never overrides a success
        }

        Status = PaymentStatus.Failed;
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

    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public static ReceivedWebhook Receive(string provider, string providerEventId, string type, string providerPaymentId, string payload, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Provider = provider,
        ProviderEventId = providerEventId,
        Type = type,
        ProviderPaymentId = providerPaymentId,
        Payload = payload,
        ReceivedAt = now.ToUniversalTime(),
    };

    public void MarkProcessed(DateTimeOffset now) => ProcessedAt = now.ToUniversalTime();
}
