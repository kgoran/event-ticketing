using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventTicketing.Payments.Application;
using Microsoft.Extensions.Options;

namespace EventTicketing.Payments.Infrastructure.Providers;

/// <summary>Configuration section "Payments:LocalPay".</summary>
internal sealed class LocalPayOptions
{
    /// <summary>Resolved through service discovery under Aspire.</summary>
    public Uri BaseUrl { get; set; } = new("http://localpay");

    /// <summary>Shared with LocalPay; signs its webhooks (HMAC-SHA256).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Webhooks older than this are refused, so a captured webhook can't be replayed later.</summary>
    public TimeSpan SignatureTolerance { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The adapter for LocalPay, our payment simulator (ADR-0004). It speaks LocalPay's published HTTP
/// API (tooling/simulators/LocalPay/openapi/LocalPay.json) and translates it into our model; it
/// shares no code with the simulator (ADR-0021).
/// </summary>
internal sealed class LocalPayProvider(HttpClient http, IOptions<LocalPayOptions> options, TimeProvider time) : IPaymentProvider
{
    public const string ProviderName = "localpay";
    public const string SignatureHeader = "LocalPay-Signature";

    public string Name => ProviderName;

    public async Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("v1/payments", UriKind.Relative))
        {
            Content = JsonContent.Create(new LocalPayCreatePayment(
                request.OrderId.ToString(),
                request.AmountMinorUnits,
                request.Currency,
                request.ReturnUrl,
                request.ExpiresAt)),
        };
        message.Headers.Add("Idempotency-Key", request.OrderId.ToString());

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payment = await response.Content.ReadFromJsonAsync<LocalPayPayment>(cancellationToken)
            ?? throw new InvalidOperationException("LocalPay returned an empty payment.");

        return new CreatePaymentResult(payment.Id, payment.CheckoutUrl);
    }

    /// <summary>
    /// POST /v1/payments/{id}/expire: 200 when expired; 409 with the payment when it already
    /// succeeded or failed. A failed payment can't be paid either, so it counts as cancelled. A
    /// payment LocalPay doesn't know (it keeps state in memory) can't be paid at all.
    /// </summary>
    public async Task<CancelPaymentResult> CancelPaymentAsync(string providerPaymentId, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(new Uri($"v1/payments/{Uri.EscapeDataString(providerPaymentId)}/expire", UriKind.Relative), null, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK or HttpStatusCode.NotFound:
                return CancelPaymentResult.Cancelled;
            case HttpStatusCode.Conflict:
                var payment = await response.Content.ReadFromJsonAsync<LocalPayPayment>(cancellationToken);
                return payment?.Status == "succeeded" ? CancelPaymentResult.AlreadyCompleted : CancelPaymentResult.Cancelled;
            default:
                response.EnsureSuccessStatusCode();
                throw new HttpRequestException($"LocalPay answered {(int)response.StatusCode} to expiring a payment.");
        }
    }

    public async Task<ProviderPaymentStatus> GetStatusAsync(string providerPaymentId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri($"v1/payments/{Uri.EscapeDataString(providerPaymentId)}", UriKind.Relative), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return ProviderPaymentStatus.Expired;
        }

        response.EnsureSuccessStatusCode();
        var payment = await response.Content.ReadFromJsonAsync<LocalPayPayment>(cancellationToken)
            ?? throw new InvalidOperationException("LocalPay returned an empty payment.");

        return StatusOf(payment.Status);
    }

    /// <summary>
    /// POST /v1/refunds with our refund ID as the Idempotency-Key. LocalPay refunds at once (201, or 200
    /// for a repeated key); 409 means it refuses, e.g. the payment didn't succeed or is already refunded.
    /// </summary>
    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("v1/refunds", UriKind.Relative))
        {
            Content = JsonContent.Create(new LocalPayCreateRefund(request.ProviderPaymentId, request.AmountMinorUnits, request.Reason)),
        };
        message.Headers.Add("Idempotency-Key", request.RefundId.ToString());

        using var response = await http.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var problem = await response.Content.ReadFromJsonAsync<LocalPayProblem>(cancellationToken);
            return new RefundResult(ProviderRefundStatus.Failed, null, problem?.Detail ?? problem?.Title ?? "LocalPay refused the refund.");
        }

        response.EnsureSuccessStatusCode();
        var refund = await response.Content.ReadFromJsonAsync<LocalPayRefund>(cancellationToken)
            ?? throw new InvalidOperationException("LocalPay returned an empty refund.");

        return new RefundResult(refund.Status == "succeeded" ? ProviderRefundStatus.Succeeded : ProviderRefundStatus.Pending, refund.Id);
    }

    /// <summary>LocalPay gets a payment-listing API with reconciliation in M4 (M1a scope).</summary>
    public async IAsyncEnumerable<ProviderPayment> ListPaymentsAsync(DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("LocalPay can't list payments yet; reconciliation brings it in M4.");
#pragma warning disable CS0162 // Unreachable code: the method must stay an iterator.
        yield break;
#pragma warning restore CS0162
    }

    public ProviderWebhook? VerifyWebhook(string payload, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!headers.TryGetValue(SignatureHeader, out var header) || !TryParseSignature(header, out var timestamp, out var signature))
        {
            return null;
        }

        var age = time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (age.Duration() > options.Value.SignatureTolerance)
        {
            return null;
        }

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.Value.WebhookSecret),
            Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{payload}"));

        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            return null;
        }

        var webhook = JsonSerializer.Deserialize<LocalPayWebhook>(payload, JsonSerializerOptions.Web);
        return webhook is null
            ? null
            : new ProviderWebhook(webhook.Id, webhook.Type, webhook.Data.PaymentId, webhook.Data.Reference, webhook.Data.AmountMinorUnits, webhook.Data.Currency, webhook.Data.RefundId);
    }

    private static ProviderPaymentStatus StatusOf(string status) => status switch
    {
        "requires_payment" => ProviderPaymentStatus.Open,
        "succeeded" => ProviderPaymentStatus.Succeeded,
        "failed" => ProviderPaymentStatus.Failed,
        "expired" => ProviderPaymentStatus.Expired,
        _ => throw new InvalidOperationException($"LocalPay reported an unknown payment status '{status}'."),
    };

    /// <summary>Parses "t=1700000000,v1=hex".</summary>
    private static bool TryParseSignature(string header, out long timestamp, out byte[] signature)
    {
        timestamp = 0;
        signature = [];
        string? t = null;
        string? v1 = null;

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0] == "t")
            {
                t = pair[1];
            }
            else if (pair[0] == "v1")
            {
                v1 = pair[1];
            }
        }

        if (t is null || v1 is null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
        {
            return false;
        }

        try
        {
            signature = Convert.FromHexString(v1);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record LocalPayCreatePayment(string Reference, long AmountMinorUnits, string Currency, string ReturnUrl, DateTimeOffset ExpiresAt);

    private sealed record LocalPayPayment(string Id, string CheckoutUrl, string Status);

    private sealed record LocalPayWebhook(string Id, string Type, LocalPayWebhookData Data);

    private sealed record LocalPayWebhookData(string PaymentId, string Reference, long AmountMinorUnits, string Currency, string? RefundId = null);

    private sealed record LocalPayCreateRefund(string PaymentId, long AmountMinorUnits, string Reason);

    private sealed record LocalPayRefund(string Id, string Status);

    private sealed record LocalPayProblem(string? Title, string? Detail);
}
