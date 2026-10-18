using EventTicketing.Inventory.Contracts;
using EventTicketing.Orders.Domain;
using EventTicketing.Payments.Contracts;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Application.Scheduling;
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
/// Asking again for a hold that already has a Pending order resumes that order (a retry after a
/// timeout, a lost provider response, a double click): Payments returns the same session.
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
    IOrderRepository orders,
    ICommandHandler<CreateOrder, CreatedOrder> createOrder,
    IExecutionContext caller,
    CheckoutPolicy policy)
    : ICommandHandler<StartCheckout, CheckoutStarted>
{
    public async Task<Result<CheckoutStarted>> HandleAsync(StartCheckout command, CancellationToken cancellationToken)
    {
        var customerId = caller.UserId ?? throw new InvalidOperationException("Checkout needs a signed-in caller.");

        if (await orders.FindByHoldAsync(command.HoldId, cancellationToken) is { } existing)
        {
            return await ResumeAsync(existing, customerId, cancellationToken);
        }

        var orderId = Guid.CreateVersion7();
        var checkout = await inventory.BeginCheckoutAsync(command.HoldId, orderId, customerId, cancellationToken);
        if (checkout.Outcome != BeginCheckoutOutcome.Started)
        {
            // Two requests for the same hold at once: the other one may have created the order meanwhile.
            return await orders.FindByHoldAsync(command.HoldId, cancellationToken) is { } concurrent
                ? await ResumeAsync(concurrent, customerId, cancellationToken)
                : Error.Conflict("checkout.hold_not_active", "The hold has expired or isn't yours.");
        }

        var created = await createOrder.HandleAsync(
            new CreateOrder(orderId, command.HoldId, checkout.EventId, [.. checkout.Seats.Select(seat => new OrderSeat(seat.SeatId, seat.PriceCategoryId))]),
            cancellationToken);

        if (created.IsFailure)
        {
            return created.Error;
        }

        return await PaymentPageAsync(orderId, created.Value.TotalMinorUnits, created.Value.Currency, cancellationToken);
    }

    private async Task<Result<CheckoutStarted>> ResumeAsync(Order order, string customerId, CancellationToken cancellationToken)
    {
        if (order.CustomerId != customerId)
        {
            return Error.Conflict("checkout.hold_not_active", "The hold has expired or isn't yours.");
        }

        if (order.Status != OrderStatus.Pending)
        {
            return Error.Conflict("checkout.order_closed", $"This hold's order is already {order.Status.ToString().ToLowerInvariant()}.");
        }

        return await PaymentPageAsync(order.Id, order.TotalMinorUnits, order.Currency, cancellationToken);
    }

    /// <summary>Payments' session for the order: created now, or the one created before (the order ID is the idempotency key).</summary>
    private async Task<Result<CheckoutStarted>> PaymentPageAsync(Guid orderId, long totalMinorUnits, string currency, CancellationToken cancellationToken)
    {
        var returnUrl = policy.ReturnUrlTemplate.Replace("{orderId}", orderId.ToString(), StringComparison.Ordinal);
        var payment = await payments.CreateCheckoutAsync(new CreateCheckoutRequest(orderId, totalMinorUnits, currency, returnUrl), cancellationToken);

        return payment is { Outcome: CreateCheckoutOutcome.Created, RedirectUrl: { } redirectUrl }
            ? new CheckoutStarted(orderId, redirectUrl)
            : new Error("checkout.payment_unavailable", "Payment is unavailable right now. Please try again.");
    }
}

/// <summary>
/// Creates the Pending order with prices from Orders' price list (ADR-0009, step 2), and schedules
/// its checkout deadline in the same transaction: an order can't exist without one.
/// </summary>
public sealed record CreateOrder(Guid OrderId, Guid HoldId, Guid EventId, IReadOnlyList<OrderSeat> Seats) : ICommand<CreatedOrder>;

public sealed record OrderSeat(Guid SeatId, Guid PriceCategoryId);

public sealed record CreatedOrder(Guid OrderId, long TotalMinorUnits, string Currency);

internal sealed class CreateOrderAuthorizer : InternalCommandAuthorizer<CreateOrder>;

internal sealed class CreateOrderHandler(
    IOrderRepository orders,
    IPriceList priceList,
    IMessageScheduler scheduler,
    IExecutionContext caller,
    CheckoutPolicy policy,
    TimeProvider time)
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
        var now = time.GetUtcNow();
        var order = Order.Place(
            command.OrderId,
            caller.UserId ?? throw new InvalidOperationException("An order needs a signed-in caller."),
            command.HoldId,
            command.EventId,
            currency,
            [.. command.Seats.Select(seat => (seat.SeatId, seat.PriceCategoryId, new Money(prices[seat.PriceCategoryId].PriceMinorUnits, currency)))],
            now,
            now + policy.PaymentDeadline);

        orders.Add(order);
        scheduler.Schedule(new CheckoutDeadline(order.Id), order.PaymentDeadline);
        return new CreatedOrder(order.Id, order.TotalMinorUnits, order.Currency);
    }
}
