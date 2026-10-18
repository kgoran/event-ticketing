using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace EventTicketing.Tooling.LocalPay;

public sealed record CreatePayment(string Reference, long AmountMinorUnits, string Currency, string ReturnUrl, DateTimeOffset ExpiresAt);

public sealed record PaymentResponse(string Id, string Reference, long AmountMinorUnits, string Currency, string Status, string CheckoutUrl);

public static class PaymentStatus
{
    public const string RequiresPayment = "requires_payment";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
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

    public string Status { get; set; } = PaymentStatus.RequiresPayment;

    public PaymentResponse ToResponse() => new(Id, Reference, AmountMinorUnits, Currency, Status, CheckoutUrl);
}

/// <summary>Payments in memory. A repeated Idempotency-Key returns the payment created the first time, like a real provider.</summary>
public sealed class PaymentStore
{
    private readonly ConcurrentDictionary<string, Payment> _payments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotencyKeys = new(StringComparer.Ordinal);

    public Payment Create(CreatePayment request, string? idempotencyKey, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(request);

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
            };
            _payments[id] = payment;
            return payment;
        }

        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return NewPayment();
        }

        var paymentId = _idempotencyKeys.GetOrAdd(idempotencyKey, _ => NewPayment().Id);
        return _payments[paymentId];
    }

    public Payment? Find(string paymentId) => _payments.GetValueOrDefault(paymentId);

    /// <summary>Approves or declines a payment that is still open; a payment already decided stays as it was.</summary>
    public Payment? Complete(string paymentId, bool succeeded)
    {
        if (!_payments.TryGetValue(paymentId, out var payment))
        {
            return null;
        }

        lock (payment)
        {
            if (payment.Status == PaymentStatus.RequiresPayment)
            {
                payment.Status = succeeded ? PaymentStatus.Succeeded : PaymentStatus.Failed;
            }
        }

        return payment;
    }
}

public sealed class WebhookOptions
{
    /// <summary>The shop's webhook endpoint, e.g. WebhookIngress's /webhooks/localpay.</summary>
    public string? Url { get; set; }

    /// <summary>Shared with the shop; signs every webhook (HMAC-SHA256).</summary>
    public string Secret { get; set; } = string.Empty;
}

/// <summary>
/// Sends signed webhooks. Header: <c>LocalPay-Signature: t={unix seconds},v1={hex HMAC-SHA256 of "t.body"}</c>.
/// </summary>
public sealed partial class WebhookSender(HttpClient http, IOptions<WebhookOptions> options, TimeProvider time, ILogger<WebhookSender> logger)
{
    public async Task SendAsync(Payment payment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (string.IsNullOrWhiteSpace(options.Value.Url))
        {
            LogNoWebhookUrl(logger, payment.Id);
            return;
        }

        var type = payment.Status == PaymentStatus.Succeeded ? "payment.succeeded" : "payment.failed";
        var body = JsonSerializer.Serialize(
            new
            {
                id = "evt_" + Guid.NewGuid().ToString("N"),
                type,
                created = time.GetUtcNow(),
                data = new { paymentId = payment.Id, reference = payment.Reference, amountMinorUnits = payment.AmountMinorUnits, currency = payment.Currency, status = payment.Status },
            },
            JsonSerializerOptions.Web);

        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.Value.Secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("LocalPay-Signature", $"t={timestamp},v1={signature}");

        // Delivery retries and the misbehaviour scenarios (late, duplicate, missing) arrive with L1.
        using var response = await http.SendAsync(request, cancellationToken);
        LogWebhookSent(logger, type, payment.Id, (int)response.StatusCode);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No webhook URL configured; payment {PaymentId} was not reported")]
    private static partial void LogNoWebhookUrl(ILogger logger, string paymentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Type} for {PaymentId}: HTTP {StatusCode}")]
    private static partial void LogWebhookSent(ILogger logger, string type, string paymentId, int statusCode);
}

public static class CheckoutPage
{
    public static string Render(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var encode = HtmlEncoder.Default;
        var amount = (payment.AmountMinorUnits / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        var id = encode.Encode(payment.Id);
        var decided = payment.Status != PaymentStatus.RequiresPayment;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>LocalPay checkout</title>
              <style>
                body { font-family: system-ui, sans-serif; max-width: 26rem; margin: 3rem auto; padding: 0 1rem; color: #1b1b1f; }
                .amount { font-size: 2rem; margin: .5rem 0 1.5rem; }
                form { display: inline; }
                button { font-size: 1rem; padding: .6rem 1.4rem; margin-right: .5rem; border-radius: .4rem; border: 1px solid #888; cursor: pointer; }
                .approve { background: #1f7a3a; color: white; border-color: #1f7a3a; }
                small { color: #666; }
              </style>
            </head>
            <body>
              <h1>LocalPay</h1>
              <p>Payment for order <code>{{encode.Encode(payment.Reference)}}</code></p>
              <div class="amount">{{amount}} {{encode.Encode(payment.Currency)}}</div>
              {{(decided
                  ? $"<p>This payment is already <strong>{encode.Encode(payment.Status)}</strong>.</p>"
                  : $"""
                    <form method="post" action="/checkout/{id}/approve"><button class="approve">Approve</button></form>
                    <form method="post" action="/checkout/{id}/decline"><button>Decline</button></form>
                    """)}}
              <p><small>A payment simulator for development and tests. No real money moves.</small></p>
            </body>
            </html>
            """;
    }
}
