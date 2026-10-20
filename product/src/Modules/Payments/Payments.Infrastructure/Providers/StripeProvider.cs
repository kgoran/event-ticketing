using System.Runtime.CompilerServices;
using EventTicketing.Payments.Application;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace EventTicketing.Payments.Infrastructure.Providers;

/// <summary>Configuration section "Payments:Stripe". Stripe is offered only when <see cref="SecretKey"/> is set (ADR-0050).</summary>
internal sealed class StripeOptions
{
    /// <summary>A test-mode secret key (sk_test_…); never committed (ADR-0025).</summary>
    public string? SecretKey { get; set; }

    /// <summary>The endpoint's signing secret (whsec_…), e.g. from <c>stripe listen --print-secret</c>.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Stripe's API, or <c>stripe-mock</c> in tests. Null means Stripe itself.</summary>
    public string? ApiBase { get; set; }

    /// <summary>Webhooks older than this are refused (Stripe's own default is 5 minutes).</summary>
    public TimeSpan SignatureTolerance { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The adapter for Stripe's hosted Checkout (ADR-0004, ADR-0050), with Stripe's own library. A
/// payment is a Checkout Session; our order ID is its client reference and the idempotency key.
/// Contract-tested against <c>stripe-mock</c>; its behaviour against real Stripe is proven only when
/// someone enables it with test keys.
/// </summary>
internal sealed class StripeProvider(IStripeClient stripe, IOptions<StripeOptions> options, TimeProvider time) : IPaymentProvider
{
    public const string ProviderName = "stripe";
    public const string SignatureHeader = "Stripe-Signature";

    /// <summary>Stripe refuses a session that expires sooner than 30 minutes after it is created; a minute of margin covers clock skew.</summary>
    private static readonly TimeSpan MinimumSessionLifetime = TimeSpan.FromMinutes(31);

    public string Name => ProviderName;

    public async Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var orderId = request.OrderId.ToString();
        var earliestExpiry = time.GetUtcNow() + MinimumSessionLifetime;
        var session = await new SessionService(stripe).CreateAsync(
            new SessionCreateOptions
            {
                Mode = "payment",
                ClientReferenceId = orderId,
                SuccessUrl = request.ReturnUrl,
                CancelUrl = request.ReturnUrl,
                ExpiresAt = (request.ExpiresAt > earliestExpiry ? request.ExpiresAt : earliestExpiry).UtcDateTime,
                LineItems =
                [
                    new SessionLineItemOptions
                    {
                        Quantity = 1,
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency = request.Currency.ToLowerInvariant(),
                            UnitAmount = request.AmountMinorUnits,
                            ProductData = new SessionLineItemPriceDataProductDataOptions { Name = $"Tickets, order {orderId[..8]}" },
                        },
                    },
                ],
                Metadata = new Dictionary<string, string> { ["orderId"] = orderId },
                PaymentIntentData = new SessionPaymentIntentDataOptions { Metadata = new Dictionary<string, string> { ["orderId"] = orderId } },
            },
            new RequestOptions { IdempotencyKey = orderId },
            cancellationToken);

        return new CreatePaymentResult(session.Id, session.Url);
    }

    // Which payment methods Checkout offers comes from the account's settings (Stripe.net 53 no
    // longer takes a list per session): allow only card and wallets there, which settle at once;
    // slower methods would complete after the hold has expired (ADR-0010).

    /// <summary>
    /// Expires the session (ADR-0010: the payment is cancelled before the seats are released). A
    /// session that completed meanwhile can't be expired: Stripe refuses, and the session says why.
    /// </summary>
    public async Task<CancelPaymentResult> CancelPaymentAsync(string providerPaymentId, CancellationToken cancellationToken)
    {
        var sessions = new SessionService(stripe);
        var session = await sessions.GetAsync(providerPaymentId, cancellationToken: cancellationToken);
        if (session.Status == "open")
        {
            try
            {
                session = await sessions.ExpireAsync(providerPaymentId, cancellationToken: cancellationToken);
            }
            catch (StripeException) when (!cancellationToken.IsCancellationRequested)
            {
                // Completed between the two calls: ask again what happened.
                session = await sessions.GetAsync(providerPaymentId, cancellationToken: cancellationToken);
            }
        }

        return StatusOf(session) == ProviderPaymentStatus.Succeeded ? CancelPaymentResult.AlreadyCompleted : CancelPaymentResult.Cancelled;
    }

    public async Task<ProviderPaymentStatus> GetStatusAsync(string providerPaymentId, CancellationToken cancellationToken) =>
        StatusOf(await new SessionService(stripe).GetAsync(providerPaymentId, cancellationToken: cancellationToken));

    public async IAsyncEnumerable<ProviderPayment> ListPaymentsAsync(DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sessions = new SessionService(stripe).ListAutoPagingAsync(
            new SessionListOptions { Created = new DateRangeOptions { GreaterThanOrEqual = from.UtcDateTime, LessThan = to.UtcDateTime } },
            cancellationToken: cancellationToken);

        await foreach (var session in sessions)
        {
            yield return new ProviderPayment(session.Id, session.ClientReferenceId ?? string.Empty, session.AmountTotal ?? 0, (session.Currency ?? string.Empty).ToUpperInvariant(), StatusOf(session));
        }
    }

    /// <summary>
    /// Refunds the session's payment intent; our refund ID is the idempotency key. The session and
    /// order go in the refund's metadata, because Stripe's refund webhooks name only the payment intent.
    /// </summary>
    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await new SessionService(stripe).GetAsync(request.ProviderPaymentId, cancellationToken: cancellationToken);
        if (session.PaymentIntentId is null)
        {
            return new RefundResult(ProviderRefundStatus.Failed, null, "The session has no payment to refund.");
        }

        try
        {
            var refund = await new RefundService(stripe).CreateAsync(
                new RefundCreateOptions
                {
                    PaymentIntent = session.PaymentIntentId,
                    Amount = request.AmountMinorUnits,
                    Reason = "requested_by_customer",
                    Metadata = new Dictionary<string, string>
                    {
                        ["refundId"] = request.RefundId.ToString(),
                        ["checkoutSession"] = request.ProviderPaymentId,
                        ["orderId"] = session.ClientReferenceId ?? string.Empty,
                        ["reason"] = request.Reason,
                    },
                },
                new RequestOptions { IdempotencyKey = request.RefundId.ToString() },
                cancellationToken);

            return new RefundResult(
                refund.Status switch
                {
                    "succeeded" => ProviderRefundStatus.Succeeded,
                    "failed" or "canceled" => ProviderRefundStatus.Failed,
                    _ => ProviderRefundStatus.Pending,
                },
                refund.Id,
                refund.FailureReason);
        }
        catch (StripeException exception) when (exception.StripeError?.Type == "invalid_request_error")
        {
            // E.g. already refunded in full: Stripe refuses for good, a retry won't change that.
            return new RefundResult(ProviderRefundStatus.Failed, null, exception.StripeError.Message);
        }
    }

    /// <summary>
    /// Checks the <c>Stripe-Signature</c> header (<c>t=…,v1=…</c>, HMAC-SHA256 over <c>t.payload</c>) and
    /// translates the events Payments acts on. Every other event is acknowledged and ignored.
    /// </summary>
    public ProviderWebhook? VerifyWebhook(string payload, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!headers.TryGetValue(SignatureHeader, out var signature) || options.Value.WebhookSecret is not { Length: > 0 } secret)
        {
            return null;
        }

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                payload,
                signature,
                secret,
                (long)options.Value.SignatureTolerance.TotalSeconds,
                time.GetUtcNow().ToUnixTimeSeconds(),
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException)
        {
            return null;
        }

        return stripeEvent.Data.Object switch
        {
            Session session when stripeEvent.Type is "checkout.session.completed" && session.PaymentStatus == "paid"
                => PaymentWebhook(stripeEvent, session, ProviderWebhookTypes.PaymentSucceeded),
            Session session when stripeEvent.Type is "checkout.session.async_payment_succeeded"
                => PaymentWebhook(stripeEvent, session, ProviderWebhookTypes.PaymentSucceeded),
            Session session when stripeEvent.Type is "checkout.session.async_payment_failed"
                => PaymentWebhook(stripeEvent, session, ProviderWebhookTypes.PaymentFailed),
            Refund refund when stripeEvent.Type is "refund.updated" or "refund.created" && refund.Status is "succeeded" or "failed"
                => new ProviderWebhook(
                    stripeEvent.Id,
                    refund.Status == "succeeded" ? ProviderWebhookTypes.RefundSucceeded : ProviderWebhookTypes.RefundFailed,
                    refund.Metadata.GetValueOrDefault("checkoutSession") ?? string.Empty,
                    refund.Metadata.GetValueOrDefault("orderId") ?? string.Empty,
                    refund.Amount,
                    refund.Currency.ToUpperInvariant(),
                    refund.Id),
            _ => new ProviderWebhook(stripeEvent.Id, ProviderWebhookTypes.Ignored, string.Empty, string.Empty, 0, string.Empty),
        };
    }

    private static ProviderWebhook PaymentWebhook(Event stripeEvent, Session session, string type) =>
        new(stripeEvent.Id, type, session.Id, session.ClientReferenceId ?? string.Empty, session.AmountTotal ?? 0, (session.Currency ?? string.Empty).ToUpperInvariant());

    /// <summary>A session is open, complete (paid, or awaiting a slow payment) or expired; it never "fails" itself.</summary>
    private static ProviderPaymentStatus StatusOf(Session session) => (session.Status, session.PaymentStatus) switch
    {
        ("complete", "paid" or "no_payment_required") => ProviderPaymentStatus.Succeeded,
        ("complete", _) or ("open", _) => ProviderPaymentStatus.Open,
        ("expired", _) => ProviderPaymentStatus.Expired,
        _ => throw new InvalidOperationException($"Stripe reported an unknown session status '{session.Status}'."),
    };
}
