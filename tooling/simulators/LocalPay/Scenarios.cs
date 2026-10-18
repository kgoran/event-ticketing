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
}

public sealed class ScenarioOptions
{
    public TimeSpan WebhookDelay { get; set; } = TimeSpan.FromSeconds(10);
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
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };
}
