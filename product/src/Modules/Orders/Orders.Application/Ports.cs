using EventTicketing.Orders.Domain;

namespace EventTicketing.Orders.Application;

public interface IOrderRepository
{
    void Add(Order order);

    Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>The order for a hold; there is at most one (a unique index).</summary>
    Task<Order?> FindByHoldAsync(Guid holdId, CancellationToken cancellationToken);
}

public interface IPriceList
{
    /// <summary>Stores the prices, except where the list already has a price Catalog set later.</summary>
    Task ApplyAsync(IReadOnlyCollection<PriceListEntry> entries, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, PriceListEntry>> ForEventAsync(Guid eventId, CancellationToken cancellationToken);
}

/// <summary>Checkout timings (ADR-0010). Policies, configurable (configuration section "Checkout").</summary>
public sealed record CheckoutPolicy
{
    /// <summary>Where the provider sends the fan back after paying; "{orderId}" is replaced.</summary>
    public string ReturnUrlTemplate { get; init; } = "http://localhost:5101/orders/{orderId}";

    /// <summary>How long the fan has to pay before the checkout closes.</summary>
    public TimeSpan PaymentDeadline { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>After the provider says a payment already completed: how long to wait for its webhook before asking.</summary>
    public TimeSpan WebhookGracePeriod { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How soon to try again when the provider can't be reached.</summary>
    public TimeSpan ProviderRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}
