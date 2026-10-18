using EventTicketing.Shared.Domain;

namespace EventTicketing.Inventory.Domain;

/// <summary>What releasing a hold from checkout comes to.</summary>
public enum CheckoutRelease
{
    /// <summary>The hold let go now; its seats must be freed.</summary>
    FreeSeats,

    /// <summary>Released or lost before; nothing left to do.</summary>
    AlreadyFree,

    /// <summary>The order's payment went through first: the seats are sold.</summary>
    AlreadyConfirmed,
}

public enum HoldStatus
{
    Active,
    InCheckout,
    Confirmed,
    SeatsLost,

    /// <summary>Its time ran out (before checkout, or at the backstop during it). Set by the sweeper or lazily.</summary>
    Expired,

    /// <summary>The fan let go of it, or its order did after the payment was cancelled.</summary>
    Released,
}

/// <summary>
/// A fan's temporary claim on some places of one event. The seat rows themselves record which hold
/// owns them and until when; they are the source of truth (ADR-0010). The hold tracks the
/// lifecycle:
/// <code>
///   Active ──BeginCheckout──► InCheckout ──Confirm──► Confirmed
///     │ │                       │ │        └─────────► SeatsLost (expired, and the seats were taken)
///     │ │                       │ └─ReleaseFromCheckout──► Released (payment cancelled)
///     │ └─Release──► Released   └─(backstop passes)──► Expired
///     └──(time runs out)──► Expired
/// </code>
/// A fan has at most one Active or InCheckout hold per event (ADR-0024).
/// </summary>
public sealed class Hold
{
    private Hold()
    {
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    public List<Guid> SeatIds { get; private set; } = [];

    public HoldStatus Status { get; private set; }

    /// <summary>The seats' own expiry, taken from the database clock when they were claimed.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public Guid? OrderId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Hold Place(Guid id, Guid eventId, string customerId, IReadOnlyCollection<Guid> seatIds, DateTimeOffset now, DateTimeOffset expiresAt) => new()
    {
        Id = id,
        EventId = eventId,
        CustomerId = customerId,
        SeatIds = [.. seatIds],
        Status = HoldStatus.Active,
        ExpiresAt = expiresAt.ToUniversalTime(),
        CreatedAt = now.ToUniversalTime(),
    };

    /// <summary>Hands the hold to an order. Repeating it for the same order is allowed.</summary>
    public Result BeginCheckout(Guid orderId, string customerId)
    {
        if (CustomerId != customerId)
        {
            return Result.Failure(Error.Conflict("hold.not_active", "This hold isn't yours or is no longer active."));
        }

        if (Status == HoldStatus.InCheckout && OrderId == orderId)
        {
            return Result.Success();
        }

        if (Status != HoldStatus.Active)
        {
            return Result.Failure(Error.Conflict("hold.not_active", "This hold isn't yours or is no longer active."));
        }

        Status = HoldStatus.InCheckout;
        OrderId = orderId;
        return Result.Success();
    }

    /// <summary>Records the expiry the seat rows now have (database clock).</summary>
    public void ExtendTo(DateTimeOffset expiresAt) => ExpiresAt = expiresAt.ToUniversalTime();

    public bool IsConfirmedFor(Guid orderId) => Status == HoldStatus.Confirmed && OrderId == orderId;

    public void Confirm(Guid orderId)
    {
        Status = HoldStatus.Confirmed;
        OrderId = orderId;
    }

    public void MarkSeatsLost() => Status = HoldStatus.SeatsLost;

    /// <summary>The order lets go after its payment was cancelled. Safe to repeat.</summary>
    public CheckoutRelease ReleaseFromCheckout()
    {
        switch (Status)
        {
            case HoldStatus.Confirmed:
                return CheckoutRelease.AlreadyConfirmed;
            case HoldStatus.InCheckout or HoldStatus.Expired:
                Status = HoldStatus.Released;
                return CheckoutRelease.FreeSeats;
            default:
                return CheckoutRelease.AlreadyFree;
        }
    }

    /// <summary>The fan lets go before checkout. Once an order has the hold, only the order can let it go.</summary>
    public Result Release()
    {
        if (Status == HoldStatus.InCheckout)
        {
            return Result.Failure(Error.Conflict("hold.in_checkout", "This hold belongs to an order in checkout."));
        }

        if (Status is not (HoldStatus.Active or HoldStatus.Expired))
        {
            return Result.Failure(Error.Conflict("hold.not_active", "This hold is no longer active."));
        }

        Status = HoldStatus.Released;
        return Result.Success();
    }
}
