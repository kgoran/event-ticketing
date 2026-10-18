using EventTicketing.Shared.Domain;

namespace EventTicketing.Orders.Domain;

public enum OrderStatus
{
    /// <summary>Waiting for the fan to pay, until the checkout deadline.</summary>
    Pending,
    Confirmed,

    /// <summary>The deadline passed unpaid: the payment was cancelled, then the seats released (ADR-0010).</summary>
    Expired,

    /// <summary>The payment was declined: it was cancelled and the seats released.</summary>
    Failed,

    /// <summary>Paid, but the hold had expired and someone else took a seat: to be refunded (backlog item P2).</summary>
    SeatsLost,
}

/// <summary>Why a checkout closed without a sale.</summary>
public enum CheckoutCloseReason
{
    Deadline,
    PaymentFailed,
}

/// <summary>
/// A fan's order for the seats of one hold, with the prices recorded when it was placed. Its state
/// machine:
/// <code>
///   Pending ──paid, seats sold──────────────► Confirmed
///      │    └─paid, seats taken meanwhile──► SeatsLost (refund, P2)
///      ├──deadline: payment cancelled, seats released──► Expired
///      └──declined: payment cancelled, seats released──► Failed
/// </code>
/// Every transition is safe to repeat; only Pending moves.
/// </summary>
public sealed class Order
{
    private readonly List<OrderLine> _lines = [];

    private Order()
    {
    }

    public Guid Id { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    public Guid HoldId { get; private set; }

    public Guid EventId { get; private set; }

    public OrderStatus Status { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public long TotalMinorUnits { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the checkout closes unpaid (ADR-0010: 10 minutes by default).</summary>
    public DateTimeOffset PaymentDeadline { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public static Order Place(
        Guid id,
        string customerId,
        Guid holdId,
        Guid eventId,
        CurrencyCode currency,
        IReadOnlyCollection<(Guid SeatId, Guid PriceCategoryId, Money Price)> seats,
        DateTimeOffset now,
        DateTimeOffset paymentDeadline)
    {
        var total = seats.Aggregate(Money.Zero(currency), (sum, seat) => sum + seat.Price);
        var order = new Order
        {
            Id = id,
            CustomerId = customerId,
            HoldId = holdId,
            EventId = eventId,
            Status = OrderStatus.Pending,
            Currency = currency.Value,
            TotalMinorUnits = total.MinorUnits,
            CreatedAt = now.ToUniversalTime(),
            PaymentDeadline = paymentDeadline.ToUniversalTime(),
        };

        order._lines.AddRange(seats.Select(seat => new OrderLine(id, seat.SeatId, seat.PriceCategoryId, seat.Price.MinorUnits)));
        return order;
    }

    /// <summary>Marks the order confirmed. Returns false if it wasn't Pending (a repeated message).</summary>
    public bool Confirm(DateTimeOffset now) => Close(OrderStatus.Confirmed, now);

    public bool MarkSeatsLost(DateTimeOffset now) => Close(OrderStatus.SeatsLost, now);

    /// <summary>Closes the checkout unsold, after its payment was cancelled and its seats released.</summary>
    public bool CloseUnsold(CheckoutCloseReason reason, DateTimeOffset now) =>
        Close(reason == CheckoutCloseReason.Deadline ? OrderStatus.Expired : OrderStatus.Failed, now);

    private bool Close(OrderStatus status, DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending)
        {
            return false;
        }

        Status = status;
        ClosedAt = now.ToUniversalTime();
        return true;
    }
}

public sealed record OrderLine(Guid OrderId, Guid SeatId, Guid PriceCategoryId, long PriceMinorUnits);

/// <summary>
/// Orders' own copy of an event's prices, built from Catalog's EventPublished and PricesChanged
/// (ADR-0007). <see cref="SetAt"/> is when Catalog set the price, so a late message can't undo a newer one.
/// </summary>
public sealed record PriceListEntry(Guid EventId, Guid PriceCategoryId, long PriceMinorUnits, string Currency, DateTimeOffset SetAt);
