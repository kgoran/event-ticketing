using static EventTicketing.Tooling.Lab.SystemTests.LabCluster;

namespace EventTicketing.Tooling.Lab.SystemTests;

/// <summary>
/// M3 IC1: the invariant checker (D58, tooling/checks) against the lab's real data. It must say
/// nothing while the data is right, and name the place when one is wrong.
/// </summary>
public sealed class InvariantCheckerTests
{
    [Fact]
    public async Task It_finds_a_sold_place_set_free_and_is_quiet_once_it_is_back()
    {
        RequireLab();
        var (_, orderId) = await M2DemoDryRunTests.BuyOneAsync();
        var seatId = await SqlAsync($"SELECT seat_id FROM inventory.seats WHERE order_id = '{orderId}' LIMIT 1");

        try
        {
            // What a bug in a release could do: the place goes back on sale while its ticket stays.
            await SqlAsync($"UPDATE inventory.seats SET status = 'Available', order_id = NULL WHERE seat_id = '{seatId}'");

            // The checker reads a replica: it sees the change once the replica has it.
            var (_, output) = await EventuallyAsync(
                () => LabAsync("check", "--invariants"),
                result => result.ExitCode == 1,
                TimeSpan.FromSeconds(60),
                "the checker reports the place");
            output.Should().Contain("a ticket without a confirmed seat").And.Contain("a confirmed order whose place isn't sold to it").And.Contain(seatId);
        }
        finally
        {
            await SqlAsync($"UPDATE inventory.seats SET status = 'Sold', order_id = '{orderId}' WHERE seat_id = '{seatId}'");
        }

        await EventuallyAsync(() => LabAsync("check", "--invariants"), result => result.ExitCode == 0, TimeSpan.FromSeconds(60), "every rule holds again");
    }
}
