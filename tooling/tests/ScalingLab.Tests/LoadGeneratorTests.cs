using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Tooling.LoadGenerator.Tests;

/// <summary>The load generator keeps its schedule whatever the answers do (an open model, ADR-0017), and counts honestly.</summary>
public sealed class LoadGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Requests_fall_due_at_the_rate_whether_or_not_earlier_ones_were_answered()
    {
        var schedule = new Schedule(Rate: 50, Since: TimeSpan.FromSeconds(10));

        schedule.DueBy(TimeSpan.FromSeconds(10)).Should().Be(0);
        schedule.DueBy(TimeSpan.FromSeconds(11)).Should().Be(50);

        for (var i = 0; i < 50; i++)
        {
            schedule = schedule.Issued();
        }

        schedule.DueBy(TimeSpan.FromSeconds(11)).Should().Be(0);
        schedule.DueBy(TimeSpan.FromSeconds(11.5)).Should().Be(25, "a pause is caught up, not forgotten");
        new Schedule(Rate: 0, Since: TimeSpan.Zero).DueBy(TimeSpan.FromHours(1)).Should().Be(0);
    }

    [Theory]
    [InlineData(409, Outcome.Refused)]
    [InlineData(429, Outcome.Refused)]
    [InlineData(400, Outcome.ClientError)]
    [InlineData(404, Outcome.ClientError)]
    [InlineData(500, Outcome.Failed)]
    [InlineData(503, Outcome.Failed)]
    public void A_seat_taken_is_a_refusal_not_an_error(int statusCode, Outcome expected) =>
        Traffic.Classify(statusCode).Should().Be(expected);

    [Fact]
    public void A_503_with_retry_after_is_the_system_saying_not_now_on_purpose()
    {
        Traffic.Classify(503, retryAfter: true).Should().Be(Outcome.Refused, "a paused feature (FF1) or, later, shed load");
        Traffic.Classify(503, retryAfter: false).Should().Be(Outcome.Failed, "the gateway with no instance to send it to");
    }

    [Fact]
    public void Percentiles_are_nearest_rank()
    {
        double[] sorted = [.. Enumerable.Range(1, 100).Select(value => (double)value)];

        LoadStats.Percentile(sorted, 0.50).Should().Be(50);
        LoadStats.Percentile(sorted, 0.95).Should().Be(95);
        LoadStats.Percentile(sorted, 0.99).Should().Be(99);
        LoadStats.Percentile([7], 0.99).Should().Be(7);
        LoadStats.Percentile([], 0.99).Should().BeNull();
    }

    [Fact]
    public void Each_second_counts_what_was_offered_and_what_came_of_it()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_000));
        var stats = new LoadStats(clock);

        stats.Offered();
        stats.Offered();
        stats.Offered();
        stats.Skipped();
        stats.Started();
        stats.Started();
        stats.Completed(RequestKind.Hold, Outcome.Ok, TimeSpan.FromMilliseconds(30));
        stats.Completed(RequestKind.Browse, Outcome.Failed, TimeSpan.FromSeconds(5));
        stats.Started();
        clock.Advance(TimeSpan.FromSeconds(1));
        stats.Completed(RequestKind.Release, Outcome.Refused, TimeSpan.FromMilliseconds(10));
        clock.Advance(TimeSpan.FromSeconds(1));

        var snapshot = stats.Snapshot(new LoadSettings(3), eventId: null, seconds: 3);

        snapshot.InFlight.Should().Be(0);
        snapshot.Seconds.Should().HaveCount(3);
        var (empty, first, second) = (snapshot.Seconds[0], snapshot.Seconds[1], snapshot.Seconds[2]);
        empty.Offered.Should().Be(0);
        first.Should().Match<SecondStats>(s => s.Offered == 3 && s.Skipped == 1 && s.Completed == 2 && s.Ok == 1 && s.Failed == 1);
        first.LatencyP99Ms.Should().Be(30, "a failure has no meaningful latency");
        first.CompletedByKind["Hold"].Should().Be(1);
        first.LatencyP99MsByKind["Hold"].Should().Be(30);
        first.LatencyP99MsByKind["Browse"].Should().BeNull();
        second.Should().Match<SecondStats>(s => s.Offered == 0 && s.Refused == 1 && s.Completed == 1);
    }

    [Fact]
    public async Task Settings_outside_the_lab_s_range_are_refused()
    {
        await using var generator = new WebApplicationFactory<Program>();
        using var http = generator.CreateClient();

        (await http.PutAsJsonAsync("/settings", new LoadSettings(RequestsPerSecond: 5000), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await http.PutAsJsonAsync("/settings", new LoadSettings(10, HoldShare: 1.5), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await http.GetFromJsonAsync<LoadSettings>("/settings", Ct)).Should().Be(new LoadSettings());

        var chosen = new LoadSettings(0, 0.5, Guid.NewGuid(), Section: "B");
        (await http.PutAsJsonAsync("/settings", chosen, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.GetFromJsonAsync<LoadSettings>("/settings", Ct)).Should().Be(chosen);
    }

    [Fact]
    public async Task With_nothing_on_sale_the_load_is_offered_and_skipped_not_failed()
    {
        // No PublicApi listens at the address the generator is given: there is no event to load.
        var nowhere = new Uri("http://127.0.0.1:9/");
        await using var generator = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.ConfigureTestServices(services => services.AddSingleton(new LabUrls(nowhere, nowhere))));
        using var http = generator.CreateClient();

        (await http.PutAsJsonAsync("/settings", new LoadSettings(RequestsPerSecond: 50), Ct)).EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
        var stats = await http.GetFromJsonAsync<StatsSnapshot>("/stats?seconds=1", Ct);

        var lastSecond = stats!.Seconds.Should().ContainSingle().Subject;
        lastSecond.Offered.Should().BeInRange(40, 60, "the schedule keeps the rate");
        lastSecond.Skipped.Should().BeCloseTo(lastSecond.Offered, 2, "a request offered at the very end of a second is skipped in the next");
        lastSecond.Failed.Should().Be(0);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
