using System.Diagnostics.Metrics;
using System.Reflection;

namespace EventTicketing.ArchitectureTests;

/// <summary>
/// A histogram in seconds names its buckets (HistogramBuckets): OpenTelemetry's defaults suit
/// milliseconds, so without them every duration falls in the first bucket and each percentile on a
/// dashboard reads about 5 s. Found in the lab's Grafana (OB1), where command p99 was a flat 5 s.
/// </summary>
public sealed class TelemetryRulesTests
{
    [Fact]
    public void Histograms_in_seconds_name_their_buckets()
    {
        var violations =
            from assembly in ProductAssemblies.All
            from type in assembly.GetTypes()
            where !type.ContainsGenericParameters
            from field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            where field.FieldType == typeof(Histogram<double>)
            let histogram = (Histogram<double>?)field.GetValue(null)
            where histogram is { Unit: "s" } && histogram.Advice?.HistogramBucketBoundaries is null
            select $"{type.Name}.{field.Name} ({histogram.Name})";

        violations.Should().BeEmpty("a histogram in seconds needs buckets in seconds (HistogramBuckets)");
    }
}
