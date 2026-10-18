using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventTicketing.Shared.Infrastructure.Messaging;

/// <summary>
/// Metadata stored with each outbox and scheduled message. The W3C trace context lets the
/// consumer's span join the trace of the request that published the message.
/// </summary>
internal sealed record MessageHeaders(
    [property: JsonPropertyName("traceparent")] string? TraceParent,
    [property: JsonPropertyName("tracestate")] string? TraceState)
{
    public ActivityContext? ParentContext =>
        TraceParent is not null && ActivityContext.TryParse(TraceParent, TraceState, out var context)
            ? context
            : null;

    public static MessageHeaders FromCurrentActivity() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity
            ? new MessageHeaders(activity.Id, activity.TraceStateString)
            : new MessageHeaders(null, null);

    public static MessageHeaders FromJson(string json) =>
        JsonSerializer.Deserialize<MessageHeaders>(json, MessagingJson.Options) ?? new MessageHeaders(null, null);

    public string ToJson() => JsonSerializer.Serialize(this, MessagingJson.Options);
}
