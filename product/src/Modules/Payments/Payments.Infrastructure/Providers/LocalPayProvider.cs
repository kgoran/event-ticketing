using System.Globalization;
using System.Net.Http.Json;
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
/// API and translates it into our model; it shares no code with the simulator (ADR-0021).
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
        return webhook is null ? null : new ProviderWebhook(webhook.Id, webhook.Type, webhook.Data.PaymentId);
    }

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

    private sealed record LocalPayWebhookData(string PaymentId);
}
