using EventTicketing.Inventory.Domain;

namespace EventTicketing.Inventory.Application;

/// <summary>Holds, the aggregate root (ADR-0032: repositories only for aggregate roots).</summary>
public interface IHoldRepository
{
    void Add(Hold hold);

    Task<Hold?> FindAsync(Guid holdId, CancellationToken cancellationToken);
}

/// <summary>
/// The seat rows of each event, changed only by short conditional SQL statements (ADR-0011): a
/// claim either takes every requested seat or none, and nobody waits on a lock.
/// </summary>
public interface ISeatInventory
{
    Task AddSeatsAsync(Guid eventId, IReadOnlyList<NewSeat> seats, CancellationToken cancellationToken);

    /// <summary>Claims every seat for the hold, or none. An expired hold's seats count as free.</summary>
    Task<bool> TryClaimAsync(Guid eventId, IReadOnlyCollection<Guid> seatIds, Guid holdId, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Extends the hold, but only if it still owns all its seats and hasn't expired.</summary>
    Task<bool> TryExtendAsync(Guid holdId, int seatCount, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Sells the hold's seats to the order, but only if the hold still owns all of them.</summary>
    Task<bool> TrySellAsync(Guid holdId, Guid orderId, int seatCount, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeatOfHold>> SeatsOfHoldAsync(Guid holdId, CancellationToken cancellationToken);
}

public sealed record NewSeat(Guid SeatId, string Section, string Row, int Number, Guid PriceCategoryId);

public sealed record SeatOfHold(Guid SeatId, Guid PriceCategoryId);

/// <summary>Hold timings (ADR-0010). Policies, configurable (configuration section "Inventory").</summary>
public sealed record InventoryPolicy
{
    public TimeSpan HoldLifetime { get; init; } = TimeSpan.FromMinutes(8);

    /// <summary>How long a hold in checkout lives at most: the provider's session expiry plus a margin.</summary>
    public TimeSpan CheckoutBackstop { get; init; } = TimeSpan.FromMinutes(35);

    public int MaxSeatsPerHold { get; init; } = 8;
}
