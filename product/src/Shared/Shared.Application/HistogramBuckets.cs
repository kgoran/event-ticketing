using System.Diagnostics.Metrics;

namespace EventTicketing.Shared.Application;

/// <summary>
/// Bucket boundaries for histograms measured in seconds. OpenTelemetry's defaults (0, 5, 10, 25 …
/// 10,000) suit milliseconds: a duration in seconds falls in the first bucket, and every percentile
/// reads about 5 s. ASP.NET Core's own durations name their buckets the same way.
/// </summary>
public static class HistogramBuckets
{
    /// <summary>A request's or a handler's time: 5 ms to 10 s, as ASP.NET Core's http.server.request.duration.</summary>
    public static readonly InstrumentAdvice<double> Seconds = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10],
    };

    /// <summary>Background delays, such as a message's lag: 50 ms to 5 minutes.</summary>
    public static readonly InstrumentAdvice<double> Delays = new()
    {
        HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300],
    };
}
