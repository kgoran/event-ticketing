using EventTicketing.Shared.Domain;

namespace EventTicketing.Orders.Domain;

public enum OrderStatus
{
    Pending,
    Confirmed,

    /// <summary>Paid, but the hold had expired and someone else took a seat: to be refunded (backlog item P2).</summary>
    SeatsLost,
}

/// <summary>A fan's order for the seats of one hold, with the prices recorded when it was placed.</summary>
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

    public IReadOnlyList<OrderLine> Lines => _lines;

    public static Order Place(
        Guid id,
        string customerId,
        Guid holdId,
        Guid eventId,
        CurrencyCode currency,
        IReadOnlyCollection<(Guid SeatId, Guid PriceCategoryId, Money Price)> seats,
        DateTimeOffset now)
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
        };

        order._lines.AddRange(seats.Select(seat => new OrderLine(id, seat.SeatId, seat.PriceCategoryId, seat.Price.MinorUnits)));
        return order;
    }

    /// <summary>Marks the order confirmed. Returns false if it already was (a repeated message).</summary>
    public bool Confirm()
    {
        if (Status == OrderStatus.Confirmed)
        {
            return false;
        }

        Status = OrderStatus.Confirmed;
        return true;
    }

    public void MarkSeatsLost() => Status = OrderStatus.SeatsLost;
}

public sealed record OrderLine(Guid OrderId, Guid SeatId, Guid PriceCategoryId, long PriceMinorUnits);

/// <summary>Orders' own copy of an event's prices, built from Catalog's EventPublished (ADR-0007).</summary>
public sealed record PriceListEntry(Guid EventId, Guid PriceCategoryId, long PriceMinorUnits, string Currency);
