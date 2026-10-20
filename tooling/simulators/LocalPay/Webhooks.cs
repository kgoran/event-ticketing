using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace EventTicketing.Tooling.LocalPay;

public sealed class WebhookOptions
{
    /// <summary>The shop's webhook endpoint, e.g. WebhookIngress's /webhooks/localpay.</summary>
    public string? Url { get; set; }

    /// <summary>Shared with the shop; signs every webhook (HMAC-SHA256).</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Delivery attempts per webhook while the shop doesn't answer 2xx.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Wait before the first retry; it doubles for each one after.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}

/// <param name="Id">The event's ID. A duplicate delivery repeats it, so the shop can drop it.</param>
/// <param name="Type">payment.succeeded, payment.failed or refund.succeeded.</param>
public sealed record WebhookEvent(string Id, string Type, DateTimeOffset Created, object Data);

/// <summary>
/// The signature header: <c>LocalPay-Signature: t={unix seconds},v1={hex HMAC-SHA256 of "t.body"}</c>.
/// The timestamp lets the shop refuse old, replayed webhooks.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "LocalPay-Signature";

    public static string Create(string secret, DateTimeOffset at, string body)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
        return $"t={timestamp},v1={signature}";
    }
}

/// <summary>Turns payment changes into webhooks, applying each payment's scenario.</summary>
public sealed class Webhooks(WebhookQueue queue, IOptions<ScenarioOptions> scenarios, TimeProvider time)
{
    public void PaymentChanged(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.Scenario == Scenario.NoWebhook)
        {
            return;
        }

        var webhook = new WebhookEvent(
            "evt_" + Guid.NewGuid().ToString("N"),
            payment.Status == PaymentStatus.Succeeded ? "payment.succeeded" : "payment.failed",
            time.GetUtcNow(),
            new { paymentId = payment.Id, reference = payment.Reference, amountMinorUnits = payment.AmountMinorUnits, currency = payment.Currency, status = payment.Status });

        var delay = payment.Scenario == Scenario.DelayedWebhook ? scenarios.Value.WebhookDelay : TimeSpan.Zero;
        queue.Enqueue(webhook, delay);
        if (payment.Scenario == Scenario.DuplicateWebhook)
        {
            queue.Enqueue(webhook, delay);
        }

        // Scenario .08: an earlier attempt's failure, reported after the success and dated before it.
        if (payment.Scenario == Scenario.StaleFailureAfterSuccess && payment.Status == PaymentStatus.Succeeded)
        {
            queue.Enqueue(
                webhook with
                {
                    Id = "evt_" + Guid.NewGuid().ToString("N"),
                    Type = "payment.failed",
                    Created = webhook.Created.AddSeconds(-30),
                    Data = new { paymentId = payment.Id, reference = payment.Reference, amountMinorUnits = payment.AmountMinorUnits, currency = payment.Currency, status = PaymentStatus.Failed },
                },
                TimeSpan.FromMilliseconds(200));
        }
    }

    public void Refunded(Refund refund)
    {
        ArgumentNullException.ThrowIfNull(refund);

        queue.Enqueue(new WebhookEvent(
            "evt_" + Guid.NewGuid().ToString("N"),
            "refund.succeeded",
            time.GetUtcNow(),
            new
            {
                refundId = refund.Id,
                paymentId = refund.Payment.Id,
                reference = refund.Payment.Reference,
                amountMinorUnits = refund.AmountMinorUnits,
                currency = refund.Payment.Currency,
                status = "succeeded",
            }));
    }
}

/// <summary>Webhooks waiting to be delivered. Like a real provider, delivery never holds up the fan's request.</summary>
public sealed class WebhookQueue
{
    private readonly Channel<(WebhookEvent Webhook, TimeSpan Delay)> _channel = Channel.CreateUnbounded<(WebhookEvent, TimeSpan)>();

    internal ChannelReader<(WebhookEvent Webhook, TimeSpan Delay)> Reader => _channel.Reader;

    public void Enqueue(WebhookEvent webhook, TimeSpan delay = default) => _channel.Writer.TryWrite((webhook, delay));
}

/// <summary>Delivers queued webhooks, each on its own: after its delay, signed, retried with backoff until the shop answers 2xx.</summary>
public sealed partial class WebhookDispatcher(
    WebhookQueue queue,
    IHttpClientFactory clients,
    IOptions<WebhookOptions> options,
    TimeProvider time,
    ILogger<WebhookDispatcher> logger) : BackgroundService
{
    public const string ClientName = "webhooks";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (webhook, delay) in queue.Reader.ReadAllAsync(stoppingToken))
        {
            _ = DeliverAsync(webhook, delay, stoppingToken);
        }
    }

    private async Task DeliverAsync(WebhookEvent webhook, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time, cancellationToken);
            }

            var settings = options.Value;
            if (string.IsNullOrWhiteSpace(settings.Url))
            {
                LogNoWebhookUrl(logger, webhook.Type, webhook.Id);
                return;
            }

            var body = JsonSerializer.Serialize(webhook, JsonSerializerOptions.Web);
            for (var attempt = 1; attempt <= settings.MaxAttempts; attempt++)
            {
                if (await TrySendAsync(settings, webhook, body, attempt, cancellationToken))
                {
                    return;
                }

                if (attempt < settings.MaxAttempts)
                {
                    await Task.Delay(settings.RetryDelay * Math.Pow(2, attempt - 1), time, cancellationToken);
                }
            }

            LogGaveUp(logger, webhook.Type, webhook.Id, settings.MaxAttempts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TrySendAsync(WebhookOptions settings, WebhookEvent webhook, string body, int attempt, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(WebhookSignature.Header, WebhookSignature.Create(settings.Secret, time.GetUtcNow(), body));

            using var response = await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
            LogSent(logger, webhook.Type, webhook.Id, attempt, (int)response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException exception)
        {
            LogFailed(logger, exception, webhook.Type, webhook.Id, attempt);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No webhook URL configured; {Type} {WebhookId} was not sent")]
    private static partial void LogNoWebhookUrl(ILogger logger, string type, string webhookId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Type} {WebhookId} (attempt {Attempt}): HTTP {StatusCode}")]
    private static partial void LogSent(ILogger logger, string type, string webhookId, int attempt, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending {Type} {WebhookId} failed (attempt {Attempt})")]
    private static partial void LogFailed(ILogger logger, Exception exception, string type, string webhookId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gave up on {Type} {WebhookId} after {Attempts} attempts")]
    private static partial void LogGaveUp(ILogger logger, string type, string webhookId, int attempts);
}
