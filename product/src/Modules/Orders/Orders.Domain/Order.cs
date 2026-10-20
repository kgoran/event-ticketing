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

    /// <summary>Paid, but there are no seats to sell for it: the money is on its way back.</summary>
    Refunding,
    Refunded,

    /// <summary>The provider refused the refund: a person has to sort it out.</summary>
    RefundFailed,
}

/// <summary>Why a paid order is refunded.</summary>
public enum RefundReason
{
    /// <summary>The hold expired before the payment was confirmed and someone else took a seat (ADR-0009).</summary>
    SeatsLost,

    /// <summary>The money arrived after the checkout had closed (a race at the deadline; ADR-0010).</summary>
    PaidAfterClose,
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
///      │    └─paid, seats taken meanwhile──► Refunding ──► Refunded | RefundFailed
///      ├──deadline: payment cancelled, seats released──► Expired ─┐
///      └──declined: payment cancelled, seats released──► Failed ──┴─paid after all──► Refunding
/// </code>
/// Every transition is safe to repeat: a repeated message finds the order already moved on.
/// </summary>
public sealed class Order
{
    private readonly List<OrderLine> _lines = [];

    private Order()
    {
    }

    public Guid Id { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>
    /// Where to send the fan's tickets and news about the order (ADR-0007: events carry it, so
    /// Notifications never asks). From the fan's token at checkout; null for orders placed before
    /// M1b or for a token without one.
    /// </summary>
    public string? CustomerEmail { get; private set; }

    public Guid HoldId { get; private set; }

    public Guid EventId { get; private set; }

    /// <summary>The event's name when the order was placed, for messages to the fan.</summary>
    public string? EventName { get; private set; }

    public OrderStatus Status { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public long TotalMinorUnits { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the checkout closes unpaid (ADR-0010: 10 minutes by default).</summary>
    public DateTimeOffset PaymentDeadline { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public RefundReason? RefundReason { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public static Order Place(
        Guid id,
        string customerId,
        string? customerEmail,
        Guid holdId,
        Guid eventId,
        string? eventName,
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
            CustomerEmail = customerEmail,
            HoldId = holdId,
            EventId = eventId,
            EventName = eventName,
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

    /// <summary>
    /// The order was paid but can't get its seats: from Pending (seats lost) or from a closed
    /// checkout (paid after close). Returns false if it is already being refunded or past that.
    /// </summary>
    public bool StartRefund(RefundReason reason, DateTimeOffset now)
    {
        if (Status is not (OrderStatus.Pending or OrderStatus.Expired or OrderStatus.Failed))
        {
            return false;
        }

        Status = OrderStatus.Refunding;
        RefundReason = reason;
        ClosedAt ??= now.ToUniversalTime();
        return true;
    }

    /// <summary>Returns false unless the order was Refunding (a repeated message).</summary>
    public bool CompleteRefund() => MoveFromRefunding(OrderStatus.Refunded);

    public bool FailRefund() => MoveFromRefunding(OrderStatus.RefundFailed);

    private bool MoveFromRefunding(OrderStatus status)
    {
        if (Status != OrderStatus.Refunding)
        {
            return false;
        }

        Status = status;
        return true;
    }

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

/// <summary>What Orders keeps about an event besides its prices: its name, for the order's messages (ADR-0007).</summary>
public sealed record KnownEvent(Guid EventId, string Name, DateTimeOffset SetAt);
