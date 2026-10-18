using EventTicketing.Orders.Domain;

namespace EventTicketing.Orders.Application;

public interface IOrderRepository
{
    void Add(Order order);

    Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken);
}

public interface IPriceList
{
    void Add(IEnumerable<PriceListEntry> entries);

    Task<IReadOnlyDictionary<Guid, PriceListEntry>> ForEventAsync(Guid eventId, CancellationToken cancellationToken);
}

/// <summary>Configuration section "Checkout".</summary>
public sealed record CheckoutPolicy
{
    /// <summary>Where the provider sends the fan back after paying; "{orderId}" is replaced.</summary>
    public string ReturnUrlTemplate { get; init; } = "http://localhost:5101/orders/{orderId}";
}
