using System.Diagnostics;
using OpenTelemetry;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Database and cache calls that belong to no request and no message are not exported: they are
/// background loops polling (the outbox, the schedule, availability snapshots, every second), and in
/// the lab they were 95% of all traces, burying the requests'. Inside a request or a message the
/// same calls are its children and are exported with it. Only these sources: an HTTP call can start a
/// trace that matters (the fan site's calls to PublicApi).
/// </summary>
internal sealed class BackgroundCallsFilter : BaseProcessor<Activity>
{
    public static readonly IReadOnlySet<string> Sources = new HashSet<string>(StringComparer.Ordinal)
    {
        "Npgsql",
        "OpenTelemetry.Instrumentation.StackExchangeRedis",
    };

    public override void OnEnd(Activity data)
    {
        if (data.ParentSpanId == default && Sources.Contains(data.Source.Name))
        {
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
