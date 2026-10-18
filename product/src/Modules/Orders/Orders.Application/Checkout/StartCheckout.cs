using EventTicketing.Inventory.Contracts;
using EventTicketing.Orders.Domain;
using EventTicketing.Payments.Contracts;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using FluentValidation;

namespace EventTicketing.Orders.Application.Checkout;

/// <summary>
/// The checkout coordinator (ADR-0009). Each step commits in its own module; no transaction spans
/// them, and every crash point recovers: the hold expires on its own, and every step is safe to repeat.
/// <list type="number">
/// <item>Inventory moves the hold into checkout for a new order ID.</item>
/// <item>Orders creates the order with the prices from its own price list.</item>
/// <item>Payments creates the provider session and returns the URL the fan pays on.</item>
/// </list>
/// </summary>
public sealed record StartCheckout(Guid HoldId) : ICommand<CheckoutStarted>, INonTransactionalCommand;

public sealed record CheckoutStarted(Guid OrderId, string RedirectUrl);

internal sealed class StartCheckoutValidator : AbstractValidator<StartCheckout>
{
    public StartCheckoutValidator() => RuleFor(command => command.HoldId).NotEmpty();
}

internal sealed class StartCheckoutAuthorizer : RoleAuthorizer<StartCheckout>
{
    protected override string Role => Roles.Fan;
}

internal sealed class StartCheckoutHandler(
    IInventoryApi inventory,
    IPaymentsApi payments,
    ICommandHandler<CreateOrder, CreatedOrder> createOrder,
    IExecutionContext caller,
    CheckoutPolicy policy)
    : ICommandHandler<StartCheckout, CheckoutStarted>
{
    public async Task<Result<CheckoutStarted>> HandleAsync(StartCheckout command, CancellationToken cancellationToken)
    {
        var customerId = caller.UserId ?? throw new InvalidOperationException("Checkout needs a signed-in caller.");
        var orderId = Guid.CreateVersion7();

        var checkout = await inventory.BeginCheckoutAsync(command.HoldId, orderId, customerId, cancellationToken);
        if (checkout.Outcome != BeginCheckoutOutcome.Started)
        {
            return Error.Conflict("checkout.hold_not_active", "The hold has expired or isn't yours.");
        }

        var created = await createOrder.HandleAsync(
            new CreateOrder(orderId, command.HoldId, checkout.EventId, [.. checkout.Seats.Select(seat => new OrderSeat(seat.SeatId, seat.PriceCategoryId))]),
            cancellationToken);

        if (created.IsFailure)
        {
            return created.Error;
        }

        var returnUrl = policy.ReturnUrlTemplate.Replace("{orderId}", orderId.ToString(), StringComparison.Ordinal);
        var payment = await payments.CreateCheckoutAsync(
            new CreateCheckoutRequest(orderId, created.Value.TotalMinorUnits, created.Value.Currency, returnUrl),
            cancellationToken);

        return payment is { Outcome: CreateCheckoutOutcome.Created, RedirectUrl: { } redirectUrl }
            ? new CheckoutStarted(orderId, redirectUrl)
            : new Error("checkout.payment_unavailable", "Payment is unavailable right now. Please try again.");
    }
}

/// <summary>Creates the Pending order with prices from Orders' price list (ADR-0009, step 2).</summary>
public sealed record CreateOrder(Guid OrderId, Guid HoldId, Guid EventId, IReadOnlyList<OrderSeat> Seats) : ICommand<CreatedOrder>;

public sealed record OrderSeat(Guid SeatId, Guid PriceCategoryId);

public sealed record CreatedOrder(Guid OrderId, long TotalMinorUnits, string Currency);

internal sealed class CreateOrderAuthorizer : InternalCommandAuthorizer<CreateOrder>;

internal sealed class CreateOrderHandler(IOrderRepository orders, IPriceList priceList, IExecutionContext caller, TimeProvider time)
    : ICommandHandler<CreateOrder, CreatedOrder>
{
    public async Task<Result<CreatedOrder>> HandleAsync(CreateOrder command, CancellationToken cancellationToken)
    {
        var prices = await priceList.ForEventAsync(command.EventId, cancellationToken);
        if (prices.Count == 0 || command.Seats.Any(seat => !prices.ContainsKey(seat.PriceCategoryId)))
        {
            return new Error("orders.unknown_price", "Prices for this event aren't known yet.");
        }

        var currency = CurrencyCode.Create(prices.Values.First().Currency).Value;
        var order = Order.Place(
            command.OrderId,
            caller.UserId ?? throw new InvalidOperationException("An order needs a signed-in caller."),
            command.HoldId,
            command.EventId,
            currency,
            [.. command.Seats.Select(seat => (seat.SeatId, seat.PriceCategoryId, new Money(prices[seat.PriceCategoryId].PriceMinorUnits, currency)))],
            time.GetUtcNow());

        orders.Add(order);
        return new CreatedOrder(order.Id, order.TotalMinorUnits, order.Currency);
    }
}
