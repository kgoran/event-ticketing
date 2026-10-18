using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EventTicketing.Shared.Infrastructure.Messaging;

/// <summary>Spans and metrics for message delivery. ServiceDefaults subscribes to every "EventTicketing.*" source and meter.</summary>
internal static class MessagingTelemetry
{
    public const string Name = "EventTicketing.Messaging";

    public static readonly ActivitySource ActivitySource = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Delivered = Meter.CreateCounter<long>(
        "eventticketing.messages.delivered", description: "Messages delivered to all their handlers.");

    public static readonly Counter<long> Retried = Meter.CreateCounter<long>(
        "eventticketing.messages.retried", description: "Messages whose delivery failed and will be retried.");

    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "eventticketing.messages.dead_lettered", description: "Messages given up on after the maximum number of attempts.");

    public static readonly Counter<long> Duplicates = Meter.CreateCounter<long>(
        "eventticketing.messages.duplicates", description: "Deliveries dropped by the inbox because the consumer had already handled the message.");

    public static readonly Histogram<double> Lag = Meter.CreateHistogram<double>(
        "eventticketing.messages.lag", unit: "s", description: "Time from an event occurring to its delivery.");

    public static Activity? StartProcessing(string operation, string messageType, Guid messageId, MessageHeaders headers)
    {
        var activity = ActivitySource.StartActivity(
            $"{operation} {messageType}",
            ActivityKind.Consumer,
            headers.ParentContext ?? default);

        activity?.SetTag("messaging.message.id", messageId.ToString());
        activity?.SetTag("eventticketing.message.type", messageType);
        return activity;
    }
}
