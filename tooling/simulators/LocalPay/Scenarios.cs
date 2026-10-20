namespace EventTicketing.Tooling.LocalPay;

/// <summary>Misbehaviour a payment asks for through its amount, like Stripe's test cards (docs/payments.md).</summary>
public enum Scenario
{
    None,

    /// <summary>Amounts ending in .01: approving declines the payment.</summary>
    Decline,

    /// <summary>Amounts ending in .02: the webhook is sent late (Scenarios:WebhookDelay, default 10 s).</summary>
    DelayedWebhook,

    /// <summary>Amounts ending in .03: the webhook is sent twice, with the same event ID.</summary>
    DuplicateWebhook,

    /// <summary>Amounts ending in .04: no webhook at all; only the status API tells.</summary>
    NoWebhook,

    /// <summary>Amounts ending in .05: when the shop expires the payment, the fan's payment succeeds just before.</summary>
    CompletesDuringExpiry,

    /// <summary>
    /// Amounts ending in .06: the first request to create the payment fails with HTTP 500 and creates
    /// nothing; a retry with the same Idempotency-Key succeeds. Needs an Idempotency-Key.
    /// </summary>
    CreateFailsOnce,

    /// <summary>
    /// Amounts ending in .07: the payment is created, but the answer comes too late
    /// (Scenarios:CreateDelay, default 15 s), so the shop's call times out. A retry with the same
    /// Idempotency-Key gets the same payment at once.
    /// </summary>
    CreateTimesOut,

    /// <summary>
    /// Amounts ending in .08: after payment.succeeded comes a stale payment.failed (an earlier attempt
    /// that failed, reported late), dated before the success. The shop must not let it undo the success.
    /// </summary>
    StaleFailureAfterSuccess,
}

public sealed class ScenarioOptions
{
    public TimeSpan WebhookDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How late the answer to a create comes in the .07 scenario: longer than the shop's timeout.</summary>
    public TimeSpan CreateDelay { get; set; } = TimeSpan.FromSeconds(15);
}

public static class Scenarios
{
    /// <summary>The scenario is chosen by the last two digits of the amount in minor units: 25.03 EUR asks for a duplicate webhook.</summary>
    public static Scenario For(long amountMinorUnits) => (amountMinorUnits % 100) switch
    {
        1 => Scenario.Decline,
        2 => Scenario.DelayedWebhook,
        3 => Scenario.DuplicateWebhook,
        4 => Scenario.NoWebhook,
        5 => Scenario.CompletesDuringExpiry,
        6 => Scenario.CreateFailsOnce,
        7 => Scenario.CreateTimesOut,
        8 => Scenario.StaleFailureAfterSuccess,
        _ => Scenario.None,
    };

    public static string? NameOf(Scenario scenario) => scenario switch
    {
        Scenario.None => null,
        Scenario.Decline => "decline",
        Scenario.DelayedWebhook => "delayed_webhook",
        Scenario.DuplicateWebhook => "duplicate_webhook",
        Scenario.NoWebhook => "no_webhook",
        Scenario.CompletesDuringExpiry => "completes_during_expiry",
        Scenario.CreateFailsOnce => "create_fails_once",
        Scenario.CreateTimesOut => "create_times_out",
        Scenario.StaleFailureAfterSuccess => "stale_failure_after_success",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    public static string Describe(Scenario scenario) => scenario switch
    {
        Scenario.None => string.Empty,
        Scenario.Decline => "Approving will decline this payment.",
        Scenario.DelayedWebhook => "The webhook will be sent late.",
        Scenario.DuplicateWebhook => "The webhook will be sent twice.",
        Scenario.NoWebhook => "No webhook will be sent.",
        Scenario.CompletesDuringExpiry => "If the shop expires this payment, it succeeds at that very moment instead.",
        Scenario.CreateFailsOnce => "Creating this payment failed once (HTTP 500); the shop's retry created it.",
        Scenario.CreateTimesOut => "Creating this payment answered too late the first time; the shop's retry got it.",
        Scenario.StaleFailureAfterSuccess => "After the success, a stale 'payment failed' webhook will follow.",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };
}
