using System.Text.Json;

namespace EventTicketing.Shared.Infrastructure.Messaging;

/// <summary>Settings for the outbox relay and the scheduler (configuration section "Messaging").</summary>
public sealed class MessagingOptions
{
    /// <summary>How long the relay and scheduler wait before looking again when they found nothing to do.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Messages claimed per pass, per module.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>How long a claimed message stays reserved for one relay before another may take it.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Delivery attempts before a message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>The longest wait between retries; the wait doubles per attempt up to this.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long an idempotency claim protects a running request before it may be claimed again.</summary>
    public TimeSpan IdempotencyLease { get; set; } = TimeSpan.FromMinutes(1);

    internal TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempts), MaxBackoff.TotalSeconds));
}

internal static class MessagingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
