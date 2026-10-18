using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// LocalPay's API as its published document describes it (tooling/simulators/LocalPay/openapi),
/// faked at the HTTP level: the product's real adapter talks to it. Product tests can't use the
/// simulator itself, because product never references tooling (ADR-0021). Tests decide what the
/// "fan" does (<see cref="Settle"/>) and send webhooks themselves (<see cref="Webhook"/>).
/// </summary>
internal sealed class FakeLocalPay
{
    public const string WebhookSecret = "test-webhook-secret-for-module-tests";
    public const string BaseUrl = "http://localpay.test/";

    private readonly ConcurrentDictionary<string, FakePayment> _payments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotencyKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<FakePayment, Task>> _onCreated = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _unreachable = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _refusesRefunds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FakeRefund> _refunds = new(StringComparer.Ordinal);

    /// <summary>Runs <paramref name="hook"/> when the order's payment is created, before LocalPay answers.</summary>
    public void WhenCreated(Guid orderId, Func<FakePayment, Task> hook) => _onCreated[orderId.ToString()] = hook;

    /// <summary>Calls about this payment fail as if LocalPay were down.</summary>
    public void MakeUnreachable(string paymentId) => _unreachable[paymentId] = true;

    public void MakeReachable(string paymentId) => _unreachable.TryRemove(paymentId, out _);

    /// <summary>Refunds of this payment are refused (409), as LocalPay does for payments it can't refund.</summary>
    public void RefuseRefunds(string paymentId) => _refusesRefunds[paymentId] = true;

    /// <summary>The refunds LocalPay has made for a payment (a repeated Idempotency-Key makes none).</summary>
    public IReadOnlyList<FakeRefund> RefundsOf(string paymentId) =>
        [.. _refunds.Values.Where(refund => refund.PaymentId == paymentId).DistinctBy(refund => refund.Id)];

    /// <summary>LocalPay's refund.succeeded webhook for a refund it made.</summary>
    public static (string Body, string Signature) RefundWebhook(FakeRefund refund, FakePayment payment)
    {
        ArgumentNullException.ThrowIfNull(refund);
        ArgumentNullException.ThrowIfNull(payment);

        var body = JsonSerializer.Serialize(
            new
            {
                id = "evt_" + Guid.NewGuid().ToString("N"),
                type = "refund.succeeded",
                created = DateTimeOffset.UtcNow,
                data = new
                {
                    refundId = refund.Id,
                    paymentId = payment.Id,
                    reference = payment.Reference,
                    amountMinorUnits = refund.AmountMinorUnits,
                    currency = payment.Currency,
                    status = "succeeded",
                },
            },
            JsonSerializerOptions.Web);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
        return (body, $"t={timestamp},v1={signature}");
    }

    public FakePayment PaymentOf(Guid orderId) =>
        _payments.Values.Single(payment => payment.Reference == orderId.ToString());

    /// <summary>The fan paid (succeeded) or the card was declined (failed). No webhook is sent: tests send those.</summary>
    public void Settle(FakePayment payment, string status)
    {
        ArgumentNullException.ThrowIfNull(payment);
        payment.Status = status;
    }

    /// <summary>A webhook as LocalPay sends it: the JSON body and its LocalPay-Signature header.</summary>
    public static (string Body, string Signature) Webhook(
        string type,
        FakePayment payment,
        string? eventId = null,
        long? amountMinorUnits = null,
        string secret = WebhookSecret,
        DateTimeOffset? signedAt = null)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var body = JsonSerializer.Serialize(
            new
            {
                id = eventId ?? "evt_" + Guid.NewGuid().ToString("N"),
                type,
                created = DateTimeOffset.UtcNow,
                data = new
                {
                    paymentId = payment.Id,
                    reference = payment.Reference,
                    amountMinorUnits = amountMinorUnits ?? payment.AmountMinorUnits,
                    currency = payment.Currency,
                    status = type == "payment.succeeded" ? "succeeded" : "failed",
                },
            },
            JsonSerializerOptions.Web);

        var timestamp = (signedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
        return (body, $"t={timestamp},v1={signature}");
    }

    /// <summary>A fresh handler per HttpClient handler rotation, all sharing this fake's state.</summary>
    public HttpMessageHandler CreateHandler() => new Handler(this);

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/').Split('/');

        if (request.Method == HttpMethod.Post && path is ["v1", "payments"])
        {
            var create = (await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken))!;
            var key = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : Guid.NewGuid().ToString();
            var created = false;
            var id = _idempotencyKeys.GetOrAdd(key, _ =>
            {
                created = true;
                var payment = new FakePayment(
                    "pay_" + Guid.NewGuid().ToString("N"),
                    create.GetProperty("reference").GetString()!,
                    create.GetProperty("amountMinorUnits").GetInt64(),
                    create.GetProperty("currency").GetString()!);
                _payments[payment.Id] = payment;
                return payment.Id;
            });

            var stored = _payments[id];
            if (created && _onCreated.TryRemove(stored.Reference, out var hook))
            {
                await hook(stored);
            }

            return Json(HttpStatusCode.Created, stored);
        }

        if (request.Method == HttpMethod.Post && path is ["v1", "refunds"])
        {
            var create = (await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken))!;
            var refundedPaymentId = create.GetProperty("paymentId").GetString()!;
            if (_unreachable.ContainsKey(refundedPaymentId))
            {
                throw new HttpRequestException("LocalPay is unreachable.");
            }

            var key = request.Headers.GetValues("Idempotency-Key").Single();
            if (_refunds.TryGetValue(key, out var existing))
            {
                return RefundJson(HttpStatusCode.OK, existing);
            }

            if (_refusesRefunds.ContainsKey(refundedPaymentId) || !_payments.TryGetValue(refundedPaymentId, out var paid) || paid.Status != "succeeded")
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new { title = "Conflict", detail = "This payment can't be refunded.", status = 409 }),
                };
            }

            var refund = _refunds.GetOrAdd(key, _ => new FakeRefund("re_" + Guid.NewGuid().ToString("N"), refundedPaymentId, create.GetProperty("amountMinorUnits").GetInt64()));
            return RefundJson(HttpStatusCode.Created, refund);
        }

        if (path is ["v1", "payments", var paymentId, ..])
        {
            if (_unreachable.ContainsKey(paymentId))
            {
                throw new HttpRequestException("LocalPay is unreachable.");
            }

            if (!_payments.TryGetValue(paymentId, out var payment))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Get && path.Length == 3)
            {
                return Json(HttpStatusCode.OK, payment);
            }

            if (request.Method == HttpMethod.Post && path is [_, _, _, "expire"])
            {
                lock (payment)
                {
                    if (payment.Status == "requires_payment")
                    {
                        payment.Status = "expired";
                    }
                }

                return Json(payment.Status == "expired" ? HttpStatusCode.OK : HttpStatusCode.Conflict, payment);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, FakePayment payment) => new(status)
    {
        Content = JsonContent.Create(new
        {
            id = payment.Id,
            reference = payment.Reference,
            amountMinorUnits = payment.AmountMinorUnits,
            currency = payment.Currency,
            status = payment.Status,
            checkoutUrl = $"{BaseUrl}checkout/{payment.Id}",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            refundedMinorUnits = 0,
            scenario = (string?)null,
        }),
    };

    private static HttpResponseMessage RefundJson(HttpStatusCode status, FakeRefund refund) => new(status)
    {
        Content = JsonContent.Create(new { id = refund.Id, paymentId = refund.PaymentId, amountMinorUnits = refund.AmountMinorUnits, currency = "EUR", status = "succeeded" }),
    };

    private sealed class Handler(FakeLocalPay fake) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            fake.HandleAsync(request, cancellationToken);
    }
}

internal sealed record FakeRefund(string Id, string PaymentId, long AmountMinorUnits);

internal sealed class FakePayment(string id, string reference, long amountMinorUnits, string currency)
{
    public string Id { get; } = id;

    public string Reference { get; } = reference;

    public long AmountMinorUnits { get; } = amountMinorUnits;

    public string Currency { get; } = currency;

    public string Status { get; set; } = "requires_payment";
}
