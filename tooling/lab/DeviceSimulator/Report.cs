using System.Globalization;
using System.Text;

namespace EventTicketing.Tooling.DeviceSimulator;

/// <summary>
/// A run's numbers, and whether T8 held: 99% of the devices' syncs and uploads answered, successfully,
/// within 500 ms (a failure is an answer that missed), and the revocation everywhere within 30 s.
/// </summary>
/// <param name="SteadyFrom">When the devices' first downloads are over (two intervals in): T8 is about syncing devices, and the start is reported apart.</param>
/// <param name="FirstDownload">One device's whole door list: its size, and how many entries.</param>
public sealed record Report(
    int Devices,
    IReadOnlyList<Sample> Samples,
    RevocationProbe? Probe,
    IReadOnlyList<TimeSpan> RevocationSeen,
    TimeSpan Ran,
    DateTimeOffset StartedAt,
    DateTimeOffset SteadyFrom,
    (int Bytes, int Entries) FirstDownload = default)
{
    /// <summary>The devices' syncs and uploads once they all have their lists.</summary>
    public IReadOnlyList<Sample> Steady => [.. Samples.Where(sample => sample.Kind != Kind.Download && sample.At >= SteadyFrom)];

    /// <summary>Syncs and uploads while devices were still downloading their first lists.</summary>
    public IReadOnlyList<Sample> Start => [.. Samples.Where(sample => sample.Kind != Kind.Download && sample.At < SteadyFrom)];

    public TimeSpan AnswerP99 => Percentile(Steady, 0.99);

    public int Failed => Steady.Count(sample => !sample.Ok);

    /// <summary>The share of syncs and uploads answered successfully within T8's 500 ms.</summary>
    public double InTime => Steady.Count == 0 ? 0 : (double)Steady.Count(sample => sample.Ok && sample.Took <= Simulation.AnswerTarget) / Steady.Count;

    public bool AnswersHold => InTime >= 0.99;

    public bool RevocationHolds => Probe is null || (RevocationSeen.Count == Devices && RevocationSeen.Max() <= Simulation.RevocationTarget);

    public bool Holds => AnswersHold && RevocationHolds;

    public static TimeSpan Percentile(IReadOnlyList<Sample> samples, double share)
    {
        if (samples.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var sorted = samples.Select(sample => sample.Took).Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    public string Describe()
    {
        static string Ms(TimeSpan value) => value.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"T8 over {Ran.TotalSeconds:0} s from {StartedAt.ToLocalTime():HH:mm:ss}, {Devices} devices:");
        foreach (var kind in Enum.GetValues<Kind>())
        {
            var of = Samples.Where(sample => sample.Kind == kind).ToList();
            if (of.Count > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"  {kind,-9} {of.Count,6} requests, {of.Count(s => !s.Ok),3} failed; p50 {Ms(Percentile(of, 0.5))}, p95 {Ms(Percentile(of, 0.95))}, p99 {Ms(Percentile(of, 0.99))}");
            }
        }

        if (FirstDownload.Entries > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  The door list: {FirstDownload.Entries} entries in {FirstDownload.Bytes / 1024.0:0} KB ({FirstDownload.Bytes / (double)FirstDownload.Entries:0} bytes each; T8 asks under 5 MB for 80,000 tickets).");
        }

        var start = Start;
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  The start, until {SteadyFrom.ToLocalTime():HH:mm:ss} (first downloads): {start.Count} syncs and uploads, p99 {Ms(Percentile(start, 0.99))}, {start.Count(s => !s.Ok)} failed.");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  Answers after it: {InTime:P2} of syncs and uploads within {Ms(Simulation.AnswerTarget)} (p99 {Ms(AnswerP99)}, {Failed} failed): {(AnswersHold ? "T8's 99% held" : "NOT T8's 99%")}.");
        foreach (var problem in Samples.Where(sample => !sample.Ok).GroupBy(sample => $"{sample.Kind}: {sample.Problem}"))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"    failed {problem.Key} ×{problem.Count()} at {string.Join(", ", problem.Select(s => s.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Distinct())}");
        }

        // When the slow answers came: one stall, or spread out?
        var slow = Samples.Where(sample => sample.Kind != Kind.Download && sample.Took > Simulation.AnswerTarget).GroupBy(sample => sample.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)).ToList();
        if (slow.Count > 0)
        {
            text.AppendLine("  Slow answers by second: " + string.Join(", ", slow.Select(second => $"{second.Key} ×{second.Count()}")));
        }
        if (Probe is null)
        {
            text.AppendLine("  Revocation: not probed.");
        }
        else
        {
            var reached = RevocationSeen.Count;
            var slowest = reached > 0 ? RevocationSeen.Max() : TimeSpan.Zero;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  Revocation: {reached} of {Devices} devices had it, the last after {slowest.TotalSeconds:0.0} s: {(RevocationHolds ? "within" : "NOT within")} T8's {Simulation.RevocationTarget.TotalSeconds:0} s.");
        }

        text.Append(Holds ? "T8 holds." : "T8 does NOT hold.");
        return text.ToString();
    }
}
