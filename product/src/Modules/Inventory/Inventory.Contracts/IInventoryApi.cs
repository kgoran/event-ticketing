namespace EventTicketing.Inventory.Contracts;

/// <summary>
/// Inventory's module API (ADR-0007). Only Orders calls it, to coordinate checkout (ADR-0009).
/// Designed as if it were remote: asynchronous, plain data in and out, and every call safe to repeat.
/// </summary>
public interface IInventoryApi
{
    /// <summary>
    /// Moves the fan's hold into checkout for the given order, pushes its expiry out to the backstop,
    /// and returns its seats. Repeating it for the same order returns the same seats.
    /// </summary>
    Task<BeginCheckoutResult> BeginCheckoutAsync(Guid holdId, Guid orderId, string customerId, CancellationToken cancellationToken);

    /// <summary>Sells the hold's seats to the order. Safe to repeat for the same order.</summary>
    Task<ConfirmHoldResult> ConfirmHoldAsync(Guid holdId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Frees the seats of the order's hold, after its payment was cancelled (ADR-0010). Safe to
    /// repeat; reports when the hold was already sold instead.
    /// </summary>
    Task<ReleaseHoldResult> ReleaseHoldAsync(Guid holdId, Guid orderId, CancellationToken cancellationToken);
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

public enum ReleaseHoldOutcome
{
    /// <summary>The seats are free (now, or already before this call).</summary>
    Released,

    /// <summary>The hold was confirmed for the order: the payment went through after all.</summary>
    AlreadyConfirmed,
}

public sealed record ReleaseHoldResult(ReleaseHoldOutcome Outcome);
