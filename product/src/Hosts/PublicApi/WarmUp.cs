using System.Text.Json;

namespace EventTicketing.PublicApi;

/// <summary>
/// PublicApi's warm-up (D43): what fans do most, browsing, a few dozen times, so each read's code
/// and queries are compiled before fans' requests arrive. Holds change data and need a fan's token,
/// so they aren't part of it, and their first half-minute on a new instance is slow: a burst of
/// holds onto instances that had all just started timed out for a minute (docs/lab.md, D69).
/// </summary>
internal static class PublicApiWarmUp
{
    /// <summary>Enough for .NET's tiered compilation to optimise the hot paths (it waits for 30 calls).</summary>
    private const int Rounds = 40;

    public static async Task RunAsync(HttpClient self, CancellationToken cancellationToken)
    {
        string? eventId = null;
        for (var round = 0; round < Rounds; round++)
        {
            var events = await self.GetStringAsync(new Uri("events?pageSize=20", UriKind.Relative), cancellationToken);
            eventId ??= FirstEventId(events);
            if (eventId is null)
            {
                continue; // nothing published yet: the list is all there is to warm up
            }

            await self.GetStringAsync(new Uri($"events/{eventId}", UriKind.Relative), cancellationToken);
            await self.GetStringAsync(new Uri($"events/{eventId}/availability", UriKind.Relative), cancellationToken);
        }
    }

    private static string? FirstEventId(string events)
    {
        using var page = JsonDocument.Parse(events);
        return page.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0
            ? items[0].GetProperty("eventId").GetString()
            : null;
    }
}
