using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EventTicketing.Payments.Application;

/// <summary>
/// Payments' spans and metrics (docs/observability.md). ServiceDefaults subscribes to every
/// "EventTicketing.*" source and meter.
/// </summary>
public static class PaymentsTelemetry
{
    public const string Name = "EventTicketing.Payments";

    internal static readonly ActivitySource ActivitySource = new(Name);

    private static readonly Meter Meter = new(Name);

    internal static readonly Counter<long> Webhooks = Meter.CreateCounter<long>(
        "eventticketing.payments.webhooks",
        description: "Provider webhooks received, by provider and result (stored, duplicate, invalid_signature).");

    internal static readonly Counter<long> Outcomes = Meter.CreateCounter<long>(
        "eventticketing.payments.outcomes",
        description: "Payment outcomes recorded, by status (succeeded, failed, cancelled).");

    internal static readonly Counter<long> Refunds = Meter.CreateCounter<long>(
        "eventticketing.payments.refunds",
        description: "Refund outcomes recorded, by status (succeeded, failed).");

    /// <summary>
    /// A span in the checkout's trace for work the provider's webhook set off, so the purchase reads as
    /// one trace from POST /orders to the confirmation. The webhook's own trace is linked. Null when
    /// the checkout's trace isn't known.
    /// </summary>
    internal static Activity? ContinueCheckout(string? checkoutTraceParent, string name)
    {
        if (checkoutTraceParent is null || !ActivityContext.TryParse(checkoutTraceParent, null, out var checkout))
        {
            return null;
        }

        ActivityLink[]? links = Activity.Current is { } current ? [new ActivityLink(current.Context)] : null;
        return ActivitySource.StartActivity(name, ActivityKind.Internal, checkout, links: links);
    }
}
