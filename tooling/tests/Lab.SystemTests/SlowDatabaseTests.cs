using static EventTicketing.Tooling.Lab.SystemTests.LabCluster;

namespace EventTicketing.Tooling.Lab.SystemTests;

/// <summary>
/// M3 TX1 (D57): the slow database scenario's delay reaches the hosts through Toxiproxy, in front of
/// PgBouncer, and goes when it is removed. Holds wait for the database several times each; browsing
/// reads a replica and the cache, which Toxiproxy doesn't stand in front of.
/// </summary>
public sealed class SlowDatabaseTests
{
    [Fact]
    public async Task A_delay_on_the_database_slows_holds_and_not_browsing()
    {
        RequireLab();
        try
        {
            await RestoreAsync();
            await LoadAsync(30, holdShare: 0.5);
            await Task.Delay(TimeSpan.FromSeconds(15), Ct);
            var before = await P99Async(10);

            await DatabaseDelayAsync(100);
            await Task.Delay(TimeSpan.FromSeconds(15), Ct);
            var slow = await P99Async(10);

            await DatabaseDelayAsync(0);
            await Task.Delay(TimeSpan.FromSeconds(15), Ct);
            var after = await P99Async(10);

            slow.Hold.Should().BeGreaterThan(before.Hold + 100, $"a hold waits for the database more than once (before {before.Hold:0} ms)");
            slow.Browse.Should().BeLessThan(before.Browse + 50, "browsing reads a replica and the cache, not through Toxiproxy");
            after.Hold.Should().BeLessThan(slow.Hold / 2, "without the delay, holds are fast again");
            (await TotalsAsync(45)).Failed.Should().Be(0, "a slower database makes holds slower, not failed");
        }
        finally
        {
            await RestoreAsync();
        }
    }
}
