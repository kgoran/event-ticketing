using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting.Testing;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>
/// D20: the Scaling Lab against the whole system. At a steady rate the load generator's requests are
/// all answered, none fails, and lab fans hold and release places through PublicApi.
/// </summary>
public sealed class ScalingLabTests(RunningApp running)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task At_a_steady_rate_every_request_is_answered_and_none_fails()
    {
        var (eventId, _) = await new DemoScript(running).SeededEventAsync("Demo Concert");
        using var generator = running.App.CreateHttpClient("loadgenerator", "http");

        // Holds on the standing floor only: other tests hold particular seats at the same time.
        (await generator.PutAsJsonAsync("/settings", new { requestsPerSecond = 20, holdShare = 0.5, eventId, section = "FLOOR" }, Ct))
            .EnsureSuccessStatusCode();
        JsonElement stats;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), Ct);
            stats = await generator.GetFromJsonAsync<JsonElement>(new Uri("/stats?seconds=5", UriKind.Relative), Ct);
        }
        finally
        {
            await generator.PutAsJsonAsync("/settings", new { requestsPerSecond = 0 }, CancellationToken.None);
        }

        stats.GetProperty("eventId").GetGuid().Should().Be(eventId);
        var seconds = stats.GetProperty("seconds").EnumerateArray().ToList();
        int Sum(string property) => seconds.Sum(second => second.GetProperty(property).GetInt32());
        int SumKind(string kind) => seconds.Sum(second => second.GetProperty("completedByKind").GetProperty(kind).GetInt32());

        Sum("offered").Should().BeInRange(90, 110, "5 seconds at 20 requests/s");
        Sum("skipped").Should().Be(0);
        Sum("failed").Should().Be(0);
        Sum("clientErrors").Should().Be(0);
        Sum("ok").Should().BeGreaterThan(80);
        SumKind("Browse").Should().BePositive();
        SumKind("Hold").Should().BePositive();
        SumKind("Release").Should().BePositive();

        // The lab's page, prerendered: the controls and the charts are there.
        using var lab = running.App.CreateHttpClient("scalinglab", "http");
        var page = await lab.GetStringAsync(new Uri("/", UriKind.Relative), Ct);
        page.Should().Contain("Scaling Lab").And.Contain("Requests per second").And.Contain("Hold p99");
    }
}
