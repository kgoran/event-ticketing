using System.Net.Http.Json;

namespace EventTicketing.Tooling.ScalingLab;

/// <summary>
/// The load generator's control API (tooling/lab/LoadGenerator): what it should send, and what came
/// of it. The records mirror its JSON; the AppHost system test runs both, so a mismatch fails there.
/// </summary>
public sealed class LoadGeneratorApi(HttpClient http)
{
    public Uri? BaseAddress => http.BaseAddress;

    public Task<StatsSnapshot?> StatsAsync(int seconds, CancellationToken cancellationToken) =>
        http.GetFromJsonAsync<StatsSnapshot>($"stats?seconds={seconds}", cancellationToken);

    public async Task SetAsync(LoadSettings settings, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync("settings", settings, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <param name="RequestsPerSecond">The offered rate; 0 stops the load.</param>
/// <param name="HoldShare">The share of requests that hold a place and release it at once; the rest browse.</param>
/// <param name="EventId">null: the first event on sale.</param>
/// <param name="Section">The section every hold goes for; null: any.</param>
public sealed record LoadSettings(double RequestsPerSecond, double HoldShare, Guid? EventId, string? Section);

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
    IReadOnlyDictionary<string, int> FailedByKind,
    IReadOnlyDictionary<string, double?> LatencyP99MsByKind);

public sealed record StatsSnapshot(DateTimeOffset At, LoadSettings Settings, Guid? EventId, int InFlight, IReadOnlyList<SecondStats> Seconds);
