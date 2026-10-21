namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// DP3's worked example, release 1 (expand): while this release and the one before it may both be
/// running, a write from either leaves events.time_zone and venue_time_zone equal
/// (docs/database-changes.md). Release 2 (contract) drops time_zone, and these tests with it.
/// </summary>
public sealed class ExpandContractTests(TestSystem system)
{
    [Fact]
    public async Task An_event_this_release_creates_has_the_old_column_too()
    {
        var sale = await system.OnSaleAsync();

        var columns = await system.QueryAsync<string>("SELECT time_zone || '|' || venue_time_zone FROM catalog.events WHERE id = @p0", sale.EventId);

        var (old, renamed) = (columns.Single().Split('|')[0], columns.Single().Split('|')[1]);
        renamed.Should().NotBeEmpty();
        old.Should().Be(renamed, "the previous release still reads time_zone while this one rolls out");
    }

    [Fact]
    public async Task A_change_the_previous_release_writes_reaches_the_new_column()
    {
        var sale = await system.OnSaleAsync();

        // As the previous release's EF Core model writes it: only time_zone.
        await system.QueryAsync<int>("UPDATE catalog.events SET time_zone = 'Europe/Lisbon' WHERE id = @p0 RETURNING 1", sale.EventId);

        (await system.QueryAsync<string>("SELECT venue_time_zone FROM catalog.events WHERE id = @p0", sale.EventId))
            .Should().Equal("Europe/Lisbon");
    }
}
