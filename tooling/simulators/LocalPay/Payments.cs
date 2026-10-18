using System.Collections.Concurrent;

namespace EventTicketing.Tooling.LocalPay;

/// <param name="Reference">The shop's own ID for the payment, e.g. the order ID.</param>
/// <param name="ExpiresAt">When the payment page stops accepting payment, like a Stripe Checkout Session.</param>
public sealed record CreatePayment(string Reference, long AmountMinorUnits, string Currency, string ReturnUrl, DateTimeOffset ExpiresAt);

/// <param name="Status">requires_payment, succeeded, failed or expired.</param>
/// <param name="Scenario">The misbehaviour this payment's amount asks for, if any (see <see cref="Scenarios"/>).</param>
public sealed record PaymentResponse(
    string Id,
    string Reference,
    long AmountMinorUnits,
    string Currency,
    string Status,
    string CheckoutUrl,
    DateTimeOffset ExpiresAt,
    long RefundedMinorUnits,
    string? Scenario);

/// <param name="AmountMinorUnits">Leave out to refund whatever hasn't been refunded yet.</param>
public sealed record CreateRefund(string PaymentId, long? AmountMinorUnits = null, string? Reason = null);

/// <param name="Status">Always succeeded: LocalPay refunds at once.</param>
public sealed record RefundResponse(string Id, string PaymentId, long AmountMinorUnits, string Currency, string Status);

public static class PaymentStatus
{
    public const string RequiresPayment = "requires_payment";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

public sealed class Payment
{
    public required string Id { get; init; }

    public required string Reference { get; init; }

    public long AmountMinorUnits { get; init; }

    public required string Currency { get; init; }

    public required string ReturnUrl { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public required string CheckoutUrl { get; init; }

    public Scenario Scenario { get; init; }

    public string Status { get; set; } = PaymentStatus.RequiresPayment;

    public long RefundedMinorUnits { get; set; }

    public PaymentResponse ToResponse() =>
        new(Id, Reference, AmountMinorUnits, Currency, Status, CheckoutUrl, ExpiresAt, RefundedMinorUnits, Scenarios.NameOf(Scenario));
}

public sealed class Refund
{
    public required string Id { get; init; }

    public required Payment Payment { get; init; }

    public long AmountMinorUnits { get; init; }

    public RefundResponse ToResponse() => new(Id, Payment.Id, AmountMinorUnits, Payment.Currency, "succeeded");
}

/// <summary>What happened when a payment was asked to change.</summary>
public enum Change
{
    /// <summary>The payment changed; send its webhook.</summary>
    Changed,

    /// <summary>The payment was already in a final state and stays as it was.</summary>
    Unchanged,
}

/// <summary>
/// Payments and refunds in memory; the lab is disposable anyway (ADR-0022). A repeated
/// Idempotency-Key returns what the first request created, like a real provider. Payments expire
/// lazily: an open payment past its expiry counts as expired from then on.
/// </summary>
public sealed class PaymentStore(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Payment> _payments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Refund> _refunds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotencyKeys = new(StringComparer.Ordinal);

    /// <returns>The payment, and whether this request created it (false for a repeated key).</returns>
    public (Payment Payment, bool Created) Create(CreatePayment request, string? idempotencyKey, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = false;
        Payment NewPayment()
        {
            var id = "pay_" + Guid.NewGuid().ToString("N");
            var payment = new Payment
            {
                Id = id,
                Reference = request.Reference,
                AmountMinorUnits = request.AmountMinorUnits,
                Currency = request.Currency,
                ReturnUrl = request.ReturnUrl,
                ExpiresAt = request.ExpiresAt,
                CheckoutUrl = $"{baseUrl}/checkout/{id}",
                Scenario = Scenarios.For(request.AmountMinorUnits),
            };
            _payments[id] = payment;
            created = true;
            return payment;
        }

        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return (NewPayment(), true);
        }

        var paymentId = _idempotencyKeys.GetOrAdd("payment:" + idempotencyKey, _ => NewPayment().Id);
        return (_payments[paymentId], created);
    }

    public Payment? Find(string paymentId)
    {
        if (!_payments.TryGetValue(paymentId, out var payment))
        {
            return null;
        }

        lock (payment)
        {
            ExpireIfDue(payment);
        }

        return payment;
    }

    /// <summary>
    /// The fan approved or declined. Only an open payment changes; an approval of a payment whose
    /// scenario is "decline" fails it.
    /// </summary>
    public (Payment? Payment, Change Change) Complete(string paymentId, bool approved)
    {
        if (!_payments.TryGetValue(paymentId, out var payment))
        {
            return (null, Change.Unchanged);
        }

        lock (payment)
        {
            ExpireIfDue(payment);
            if (payment.Status != PaymentStatus.RequiresPayment)
            {
                return (payment, Change.Unchanged);
            }

            payment.Status = approved && payment.Scenario != Scenario.Decline ? PaymentStatus.Succeeded : PaymentStatus.Failed;
            return (payment, Change.Changed);
        }
    }

    /// <summary>
    /// The shop expires the payment early, e.g. at its checkout deadline. With the "completes during
    /// expiry" scenario the fan's payment wins the race: it succeeds just before, and stays succeeded.
    /// </summary>
    /// <returns>The payment, and Changed if it succeeded during this call (its webhook is due).</returns>
    public (Payment? Payment, Change Change) Expire(string paymentId)
    {
        if (!_payments.TryGetValue(paymentId, out var payment))
        {
            return (null, Change.Unchanged);
        }

        lock (payment)
        {
            ExpireIfDue(payment);
            if (payment.Status != PaymentStatus.RequiresPayment)
            {
                return (payment, Change.Unchanged);
            }

            if (payment.Scenario == Scenario.CompletesDuringExpiry)
            {
                payment.Status = PaymentStatus.Succeeded;
                return (payment, Change.Changed);
            }

            payment.Status = PaymentStatus.Expired;
            return (payment, Change.Unchanged);
        }
    }

    public RefundOutcome Refund(CreateRefund request, string? idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrEmpty(idempotencyKey)
            && _idempotencyKeys.TryGetValue("refund:" + idempotencyKey, out var existingId)
            && _refunds.TryGetValue(existingId, out var existing))
        {
            return new RefundOutcome(existing, null, Repeated: true);
        }

        if (!_payments.TryGetValue(request.PaymentId, out var payment))
        {
            return new RefundOutcome(null, "No such payment.");
        }

        lock (payment)
        {
            if (payment.Status != PaymentStatus.Succeeded)
            {
                return new RefundOutcome(null, $"Only succeeded payments can be refunded; this one is {payment.Status}.");
            }

            var remaining = payment.AmountMinorUnits - payment.RefundedMinorUnits;
            var amount = request.AmountMinorUnits ?? remaining;
            if (amount <= 0 || amount > remaining)
            {
                return new RefundOutcome(null, $"Refund 1 to {remaining} minor units.");
            }

            payment.RefundedMinorUnits += amount;
            var refund = new Refund { Id = "re_" + Guid.NewGuid().ToString("N"), Payment = payment, AmountMinorUnits = amount };
            _refunds[refund.Id] = refund;
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                _idempotencyKeys["refund:" + idempotencyKey] = refund.Id;
            }

            return new RefundOutcome(refund, null);
        }
    }

    public Refund? FindRefund(string refundId) => _refunds.GetValueOrDefault(refundId);

    private void ExpireIfDue(Payment payment)
    {
        if (payment.Status == PaymentStatus.RequiresPayment && time.GetUtcNow() >= payment.ExpiresAt)
        {
            payment.Status = PaymentStatus.Expired;
        }
    }
}

/// <param name="Repeated">The same Idempotency-Key was used before; this is the refund it made.</param>
public sealed record RefundOutcome(Refund? Refund, string? Problem, bool Repeated = false);
