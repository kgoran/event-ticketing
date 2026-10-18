using EventTicketing.Inventory.Domain;

namespace EventTicketing.Inventory.Application;

/// <summary>Holds, the aggregate root (ADR-0032: repositories only for aggregate roots).</summary>
public interface IHoldRepository
{
    void Add(Hold hold);

    Task<Hold?> FindAsync(Guid holdId, CancellationToken cancellationToken);

    /// <summary>
    /// The fan's Active or InCheckout hold on the event, if any. An Active hold whose time has run
    /// out (database clock) is marked Expired first, so it no longer counts.
    /// </summary>
    Task<Hold?> FindCurrentAsync(Guid eventId, string customerId, CancellationToken cancellationToken);

    /// <summary>Marks up to <paramref name="batch"/> Active or InCheckout holds whose time has run out as Expired.</summary>
    Task<int> ExpireLapsedAsync(int batch, CancellationToken cancellationToken);
}

/// <summary>Each event's sales window, Inventory's copy of Catalog's schedule.</summary>
public interface ISalesWindows
{
    void Add(SalesWindow window);

    Task<SalesWindow?> FindAsync(Guid eventId, CancellationToken cancellationToken);
}

/// <summary>
/// The sellable places of each event (seats and standing slots), changed only by short conditional
/// SQL statements (ADR-0011): a claim either takes every place it asks for or none, and nobody
/// waits on a lock.
/// </summary>
public interface ISeatInventory
{
    Task AddPlacesAsync(Guid eventId, IReadOnlyList<NewPlace> places, CancellationToken cancellationToken);

    /// <summary>
    /// Claims every listed seat for the hold, or returns null. Seats of an expired hold count as
    /// free; nothing can be claimed outside the event's sales window.
    /// </summary>
    Task<PlaceClaim?> TryClaimAsync(Guid eventId, IReadOnlyCollection<Guid> seatIds, Guid holdId, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Claims the best-ranked free places of a section (seats or standing slots), or returns null.</summary>
    Task<PlaceClaim?> TryClaimBestAsync(Guid eventId, string section, int quantity, Guid holdId, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Whether the event is inside its sales window now (database clock).</summary>
    Task<bool> IsOnSaleAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>Frees the seats the hold still owns, so they can no longer be sold to it.</summary>
    Task ReleaseAsync(Guid holdId, CancellationToken cancellationToken);

    /// <summary>
    /// Extends the hold by <paramref name="lifetime"/>, but only if it still owns all its seats and
    /// hasn't expired. Returns the new expiry (database clock), or null.
    /// </summary>
    Task<DateTimeOffset?> TryExtendAsync(Guid holdId, int seatCount, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>
    /// Shows up to <paramref name="batch"/> expired seats as Available. They keep their hold ID, so
    /// a late confirmation can still sell them if nobody has claimed them since.
    /// </summary>
    Task<int> MarkExpiredAvailableAsync(int batch, CancellationToken cancellationToken);

    /// <summary>Sells the hold's seats to the order, but only if the hold still owns all of them.</summary>
    Task<bool> TrySellAsync(Guid holdId, Guid orderId, int seatCount, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeatOfHold>> SeatsOfHoldAsync(Guid holdId, CancellationToken cancellationToken);
}

/// <param name="Row">Null for a standing slot.</param>
/// <param name="Number">Null for a standing slot.</param>
/// <param name="Rank">Order for "best available" within the section: 1 is best.</param>
public sealed record NewPlace(Guid SeatId, string Section, string? Row, int? Number, int Rank, Guid PriceCategoryId);

/// <param name="HeldUntil">When the claim expires, by the database clock.</param>
public sealed record PlaceClaim(IReadOnlyList<Guid> SeatIds, DateTimeOffset HeldUntil);

public sealed record SeatOfHold(Guid SeatId, Guid PriceCategoryId);

/// <summary>Hold timings and limits (ADR-0010, ADR-0024). Policies, configurable (configuration section "Inventory").</summary>
public sealed record InventoryPolicy
{
    public TimeSpan HoldLifetime { get; init; } = TimeSpan.FromMinutes(8);

    /// <summary>How long a hold in checkout lives at most: the provider's session expiry plus a margin.</summary>
    public TimeSpan CheckoutBackstop { get; init; } = TimeSpan.FromMinutes(35);

    public int MaxSeatsPerHold { get; init; } = 8;

    /// <summary>How often the sweeper tidies up expired holds (ADR-0010: about every 2 s).</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>At most this many seats, and as many holds, per sweep, to keep its transaction short.</summary>
    public int SweepBatchSize { get; init; } = 1000;
}
