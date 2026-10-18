using EventTicketing.Shared.Domain;

namespace EventTicketing.Inventory.Domain;

public enum HoldStatus
{
    Active,
    InCheckout,
    Confirmed,
    SeatsLost,
}

/// <summary>
/// A fan's temporary claim on some seats of one event. The seat rows themselves record which hold
/// owns them and until when; they are the source of truth (ADR-0010). The hold tracks the
/// lifecycle: Active → InCheckout → Confirmed, or SeatsLost if it expired and the seats were taken.
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

    public DateTimeOffset ExpiresAt { get; private set; }

    public Guid? OrderId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Hold Place(Guid eventId, string customerId, IReadOnlyCollection<Guid> seatIds, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.CreateVersion7(),
        EventId = eventId,
        CustomerId = customerId,
        SeatIds = [.. seatIds],
        Status = HoldStatus.Active,
        ExpiresAt = (now + lifetime).ToUniversalTime(),
        CreatedAt = now.ToUniversalTime(),
    };

    /// <summary>Hands the hold to an order. Repeating it for the same order is allowed.</summary>
    public Result BeginCheckout(Guid orderId, string customerId, DateTimeOffset expiresAt)
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
        ExpiresAt = expiresAt.ToUniversalTime();
        return Result.Success();
    }

    public bool IsConfirmedFor(Guid orderId) => Status == HoldStatus.Confirmed && OrderId == orderId;

    public void Confirm(Guid orderId)
    {
        Status = HoldStatus.Confirmed;
        OrderId = orderId;
    }

    public void MarkSeatsLost() => Status = HoldStatus.SeatsLost;
}
