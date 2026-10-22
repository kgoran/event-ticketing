using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EventTicketing.Tooling.ScalingLab;

/// <summary>
/// The database's added delay (D57): a latency toxic on the lab's Toxiproxy, which sits between the
/// hosts and PgBouncer (platform/manifests/toxiproxy.yaml). Every answer from the database reaches
/// the hosts that much later; 0 removes it. Only in the lab cluster: under Aspire there is no
/// Toxiproxy, and nothing to slow.
/// </summary>
public sealed class DatabaseDelay(HttpClient http)
{
    public const int MaxMilliseconds = 1000;

    // The proxy platform/manifests/toxiproxy.yaml makes, and the one toxic the lab adds to it.
    private const string Toxics = "proxies/database/toxics";
    private const string Name = "slow-database";

    /// <summary>Whether the lab has a Toxiproxy (TOXIPROXY_URL, set by tooling/deploy/chart).</summary>
    public static bool Available => Environment.GetEnvironmentVariable("TOXIPROXY_URL") is { Length: > 0 };

    /// <summary>The delay in milliseconds; 0 when there is none.</summary>
    public async Task<int> GetAsync(CancellationToken cancellationToken)
    {
        var toxics = await http.GetFromJsonAsync<JsonElement>(Toxics, cancellationToken);
        return toxics.EnumerateArray()
            .Where(toxic => toxic.GetProperty("name").GetString() == Name)
            .Select(toxic => toxic.GetProperty("attributes").GetProperty("latency").GetInt32())
            .FirstOrDefault();
    }

    /// <summary>Delays every answer by <paramref name="milliseconds"/>, give or take a fifth; 0 removes the delay.</summary>
    public async Task SetAsync(int milliseconds, CancellationToken cancellationToken)
    {
        milliseconds = Math.Clamp(milliseconds, 0, MaxMilliseconds);
        if (milliseconds == 0)
        {
            using var removed = await http.DeleteAsync(new Uri($"{Toxics}/{Name}", UriKind.Relative), cancellationToken);
            if (removed.StatusCode != HttpStatusCode.NotFound)
            {
                removed.EnsureSuccessStatusCode();
            }

            return;
        }

        // Toxiproxy changes a toxic in place, or says it has none by that name.
        var attributes = new { latency = milliseconds, jitter = milliseconds / 5 };
        using var changed = await http.PostAsJsonAsync(new Uri($"{Toxics}/{Name}", UriKind.Relative), new { attributes }, cancellationToken);
        if (changed.StatusCode != HttpStatusCode.NotFound)
        {
            changed.EnsureSuccessStatusCode();
            return;
        }

        // Downstream: the database's answers are late, as from a slow or distant database.
        using var added = await http.PostAsJsonAsync(
            new Uri(Toxics, UriKind.Relative),
            new { name = Name, type = "latency", stream = "downstream", toxicity = 1.0, attributes },
            cancellationToken);
        added.EnsureSuccessStatusCode();
    }
}
