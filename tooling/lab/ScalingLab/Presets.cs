namespace EventTicketing.Tooling.ScalingLab;

/// <summary>
/// A rate held for a while; the last stage of a preset holds until something else is chosen. A
/// stage may also set the database's added delay (D57), which the preset removes when it ends.
/// </summary>
public sealed record Stage(double Rate, TimeSpan? For, int? DatabaseDelayMs = null);

/// <summary>A scripted change of rate, as in ADR-0017's presets.</summary>
public sealed record Preset(string Name, string Description, IReadOnlyList<Stage> Stages)
{
    /// <summary>Whether it slows the database, which needs the lab cluster's Toxiproxy.</summary>
    public bool SlowsTheDatabase => Stages.Any(stage => stage.DatabaseDelayMs > 0);
}

public static class Presets
{
    /// <summary>
    /// Past what one instance of each host serves on the owner's machine (about 200–250 requests/s of
    /// the default mix, 2026-10-20), so the slider can show saturation.
    /// </summary>
    public const double SliderTop = 400;

    private const double SpikeTop = 200;

    public static readonly IReadOnlyList<Preset> All =
    [
        new("Steady 20/s", "20 requests/s until you change it.", [new(20, null)]),

        // T11's step (ADR-0015): at the lab's 50 requests/s per instance at target (D56), the step that needs 3× the instances.
        new("Step 50 → 150", "50 requests/s for 30 s, then 150: the step that needs three times the instances (T11).",
            [new(50, TimeSpan.FromSeconds(30)), new(150, null)]),

        // An on-sale: from the usual traffic to 200 in 10 s, held, then back. Within today's capacity.
        new("On-sale spike", $"20 requests/s, up to {SpikeTop} within 10 s, held for 30 s, then back to 20.",
            [
                new(20, TimeSpan.FromSeconds(10)),
                .. Enumerable.Range(1, 10).Select(second => new Stage(20 + (SpikeTop - 20) * second / 10, TimeSpan.FromSeconds(1))),
                new(SpikeTop, TimeSpan.FromSeconds(29)),
                new(20, null),
            ]),

        // A slow dependency (D57, ADR-0019): the database answers 50 ms later for a minute. Requests in
        // progress rise, and the autoscaler adds instances that can't make the database faster.
        new("Slow database", "100 requests/s; after 30 s the database answers 50 ms later for a minute, then normally again.",
            [new(100, TimeSpan.FromSeconds(30)), new(100, TimeSpan.FromSeconds(60), DatabaseDelayMs: 50), new(100, null, DatabaseDelayMs: 0)]),

        // Overload, then the way back: T7 asks for normal again within 60 s after the load drops.
        new("Past capacity", $"{SliderTop} requests/s for 20 s, then back to 20: past capacity, and how long recovery takes (T7: 60 s).",
            [new(20, TimeSpan.FromSeconds(10)), new(SliderTop, TimeSpan.FromSeconds(20)), new(20, null)]),
    ];
}
