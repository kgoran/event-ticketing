namespace EventTicketing.Inventory.Contracts;

/// <summary>
/// Inventory's module API (ADR-0007). Only Orders calls it, to coordinate checkout (ADR-0009).
/// Designed as if it were remote: asynchronous, plain data in and out.
/// </summary>
public interface IInventoryApi
{
    /// <summary>Moves the caller's hold into checkout for the given order and returns its seats.</summary>
    Task<BeginCheckoutResult> BeginCheckoutAsync(Guid holdId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Sells the hold's seats to the order. Safe to repeat for the same order.</summary>
    Task<ConfirmHoldResult> ConfirmHoldAsync(Guid holdId, Guid orderId, CancellationToken cancellationToken);
}

public enum BeginCheckoutOutcome
{
    Started,
    HoldNotActive,
}

public sealed record BeginCheckoutResult(BeginCheckoutOutcome Outcome, Guid EventId, IReadOnlyList<HeldSeat> Seats);

public sealed record HeldSeat(Guid SeatId, Guid PriceCategoryId);

public enum ConfirmHoldOutcome
{
    Confirmed,

    /// <summary>The hold expired and someone else took at least one seat; the order must be refunded.</summary>
    SeatsLost,
}

public sealed record ConfirmHoldResult(ConfirmHoldOutcome Outcome);
