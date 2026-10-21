using System.Collections.Concurrent;

namespace EventTicketing.Tooling.LoadGenerator;

/// <summary>What a request was: the scenario step, so the lab can show browsing and holds apart.</summary>
public enum RequestKind
{
    Browse,
    Hold,
    Release,
}

/// <summary>How a request ended.</summary>
public enum Outcome
{
    /// <summary>2xx.</summary>
    Ok,

    /// <summary>
    /// 409, 429 or a 503 with Retry-After: refused on purpose, by the product's rules (the seats were just taken) or to
    /// protect itself (too many requests). Correct behaviour, not an error.
    /// </summary>
    Refused,

    /// <summary>Another 4xx: a bug in the generator or a contract change.</summary>
    ClientError,

    /// <summary>5xx, a timeout or no connection: the system couldn't serve it.</summary>
    Failed,
}

/// <summary>One second of the lab's view.</summary>
/// <param name="Offered">Requests the schedule asked for in this second: the slider's rate.</param>
/// <param name="Skipped">Asked for, but not sent: the generator's own limit of requests in flight was reached, or no lab fan was free.</param>
/// <param name="Completed">Requests that finished in this second, by outcome.</param>
/// <param name="LatencyP50Ms">Latency of the answered requests (ok or refused); failures have no meaningful latency.</param>
/// <param name="LatencyP99MsByKind">The same per kind: holds and browsing have different targets (ADR-0015, T1 and T4).</param>
public sealed record SecondStats(
    DateTimeOffset At,
    int Offered,
    int Skipped,
    int Completed,
    int Ok,
    int Refused,
    int ClientErrors,
    int Failed,
    double? LatencyP50Ms,
    double? LatencyP95Ms,
    double? LatencyP99Ms,
    IReadOnlyDictionary<string, int> CompletedByKind,
    IReadOnlyDictionary<string, double?> LatencyP99MsByKind);

/// <param name="EventId">The event under load: the chosen one, or the first on sale once found.</param>
/// <param name="Seconds">The last seconds, oldest first.</param>
public sealed record StatsSnapshot(DateTimeOffset At, LoadSettings Settings, Guid? EventId, int InFlight, IReadOnlyList<SecondStats> Seconds);

/// <summary>
/// The generator's numbers, in one-second buckets for the last few minutes. Both sides of an open
/// model: what was asked for (offered) and what happened (completed, by outcome and latency), so the
/// generator can never become a hidden bottleneck (ADR-0017).
/// </summary>
public sealed class LoadStats(TimeProvider time)
{
    private const int KeptSeconds = 300;

    private readonly ConcurrentDictionary<long, Bucket> _buckets = new();
    private int _inFlight;

    public int InFlight => Volatile.Read(ref _inFlight);

    public void Offered() => Current().Add(bucket => bucket.Offered++);

    public void Skipped() => Current().Add(bucket => bucket.Skipped++);

    public void Started() => Interlocked.Increment(ref _inFlight);

    public void Completed(RequestKind kind, Outcome outcome, TimeSpan latency)
    {
        Interlocked.Decrement(ref _inFlight);
        Current().Add(bucket =>
        {
            bucket.Outcomes[(int)outcome]++;
            bucket.Kinds[(int)kind]++;
            if (outcome is Outcome.Ok or Outcome.Refused)
            {
                bucket.Latencies[(int)kind].Add(latency.TotalMilliseconds);
            }
        });
    }

    public StatsSnapshot Snapshot(LoadSettings settings, Guid? eventId, int seconds)
    {
        var now = time.GetUtcNow();
        var last = now.ToUnixTimeSeconds() - 1; // the current second is still filling up
        var series = new List<SecondStats>();
        for (var second = last - Math.Clamp(seconds, 1, KeptSeconds) + 1; second <= last; second++)
        {
            series.Add(_buckets.TryGetValue(second, out var bucket) ? bucket.ToStats(second) : Bucket.Empty(second));
        }

        foreach (var old in _buckets.Keys.Where(second => second < last - KeptSeconds))
        {
            _buckets.TryRemove(old, out _);
        }

        return new StatsSnapshot(now, settings, eventId, InFlight, series);
    }

    /// <summary>The value below which the given share of the sorted values falls (nearest rank).</summary>
    internal static double? Percentile(IReadOnlyList<double> sorted, double share) =>
        sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Count) - 1, 0, sorted.Count - 1)];

    private Bucket Current() => _buckets.GetOrAdd(time.GetUtcNow().ToUnixTimeSeconds(), _ => new Bucket());

    private sealed class Bucket
    {
        public int Offered;
        public int Skipped;
        public readonly int[] Outcomes = new int[Enum.GetValues<Outcome>().Length];
        public readonly int[] Kinds = new int[Enum.GetValues<RequestKind>().Length];
        public readonly List<double>[] Latencies = [.. Enum.GetValues<RequestKind>().Select(_ => new List<double>())];

        public void Add(Action<Bucket> change)
        {
            lock (this)
            {
                change(this);
            }
        }

        public SecondStats ToStats(long second)
        {
            lock (this)
            {
                var sorted = Latencies.SelectMany(latencies => latencies).Order().ToList();
                return new SecondStats(
                    DateTimeOffset.FromUnixTimeSeconds(second),
                    Offered,
                    Skipped,
                    Outcomes.Sum(),
                    Outcomes[(int)Outcome.Ok],
                    Outcomes[(int)Outcome.Refused],
                    Outcomes[(int)Outcome.ClientError],
                    Outcomes[(int)Outcome.Failed],
                    Percentile(sorted, 0.50),
                    Percentile(sorted, 0.95),
                    Percentile(sorted, 0.99),
                    Enum.GetValues<RequestKind>().ToDictionary(kind => kind.ToString(), kind => Kinds[(int)kind]),
                    Enum.GetValues<RequestKind>().ToDictionary(kind => kind.ToString(), kind => Percentile([.. Latencies[(int)kind].Order()], 0.99)));
            }
        }

        public static SecondStats Empty(long second) => new Bucket().ToStats(second);
    }
}
