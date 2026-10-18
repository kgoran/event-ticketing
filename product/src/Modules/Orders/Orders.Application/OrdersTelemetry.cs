using System.Diagnostics.Metrics;
using EventTicketing.Orders.Domain;

namespace EventTicketing.Orders.Application;

/// <summary>
/// Orders' metrics (docs/observability.md). ServiceDefaults subscribes to every "EventTicketing.*"
/// meter. Checkout outcomes come from the command metric (StartCheckout, by error code).
/// </summary>
public static class OrdersTelemetry
{
    public const string Name = "EventTicketing.Orders";

    private static readonly Meter Meter = new(Name);

    private static readonly Counter<long> Transitions = Meter.CreateCounter<long>(
        "eventticketing.orders.transitions",
        description: "Orders moving to a new status: confirmed, expired, failed, refunding, refunded, refund_failed.");

    internal static void Moved(OrderStatus status) =>
        Transitions.Add(1, new KeyValuePair<string, object?>("eventticketing.order.status", StatusTag(status)));

    private static string StatusTag(OrderStatus status) => status switch
    {
        OrderStatus.Pending => "pending",
        OrderStatus.Confirmed => "confirmed",
        OrderStatus.Expired => "expired",
        OrderStatus.Failed => "failed",
        OrderStatus.Refunding => "refunding",
        OrderStatus.Refunded => "refunded",
        OrderStatus.RefundFailed => "refund_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
