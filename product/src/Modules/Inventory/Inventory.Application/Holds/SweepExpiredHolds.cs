using EventTicketing.Shared.Application.Scheduling;

namespace EventTicketing.Inventory.Application.Holds;

/// <summary>
/// The sweeper (ADR-0010), a recurring scheduled message. It only tidies up: an expired seat is
/// already claimable because the claim checks expiry itself. Sweeping makes the rows say so, for
/// availability, and marks lapsed holds Expired, including holds in checkout past their backstop.
/// </summary>
public sealed record SweepExpiredHolds;

internal sealed class SweepExpiredHoldsHandler(ISeatInventory seats, IHoldRepository holds, InventoryPolicy policy)
    : IScheduledMessageHandler<SweepExpiredHolds>
{
    public async Task HandleAsync(SweepExpiredHolds message, CancellationToken cancellationToken)
    {
        await seats.MarkExpiredAvailableAsync(policy.SweepBatchSize, cancellationToken);
        await holds.ExpireLapsedAsync(policy.SweepBatchSize, cancellationToken);
    }
}
