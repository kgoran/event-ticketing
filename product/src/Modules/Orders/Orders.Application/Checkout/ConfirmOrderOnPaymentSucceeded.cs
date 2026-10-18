using EventTicketing.Inventory.Contracts;
using EventTicketing.Orders.Contracts;
using EventTicketing.Orders.Domain;
using EventTicketing.Payments.Contracts;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Application.Scheduling;

namespace EventTicketing.Orders.Application.Checkout;

/// <summary>
/// After the provider confirmed the payment, sell the hold's seats to the order (ADR-0009). Inventory
/// commits on its own first; if this transaction then fails, the message is redelivered and the
/// confirmation is simply repeated, which Inventory allows for the same order. A payment that can't
/// get its seats is refunded: the refund itself runs as a coordinator (<see cref="RefundOrder"/>), so
/// this transaction stays short and no provider call happens inside it.
/// </summary>
internal sealed class ConfirmOrderOnPaymentSucceeded(
    IOrderRepository orders,
    IInventoryApi inventory,
    IIntegrationEventPublisher events,
    IMessageScheduler scheduler,
    TimeProvider time)
    : IIntegrationEventHandler<PaymentSucceeded>
{
    public async Task HandleAsync(PaymentSucceeded integrationEvent, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(integrationEvent.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment succeeded for unknown order {integrationEvent.OrderId}.");

        var now = time.GetUtcNow();
        if (order.Status is OrderStatus.Expired or OrderStatus.Failed)
        {
            // Money for a checkout that has closed and given its seats back: return it.
            Refund(order, RefundReason.PaidAfterClose, now);
            return;
        }

        if (order.Status != OrderStatus.Pending)
        {
            return;
        }

        var confirmation = await inventory.ConfirmHoldAsync(order.HoldId, order.Id, cancellationToken);
        if (confirmation.Outcome == ConfirmHoldOutcome.SeatsLost)
        {
            Refund(order, RefundReason.SeatsLost, now);
            return;
        }

        if (order.Confirm(now))
        {
            events.Publish(new OrderConfirmed(
                Guid.CreateVersion7(),
                now,
                order.Id,
                order.CustomerId,
                order.EventId,
                order.TotalMinorUnits,
                order.Currency,
                [.. order.Lines.Select(line => new ConfirmedSeat(line.SeatId, line.PriceCategoryId, line.PriceMinorUnits))]));
        }
    }

    private void Refund(Order order, RefundReason reason, DateTimeOffset now)
    {
        if (order.StartRefund(reason, now))
        {
            scheduler.Schedule(new RefundOrder(order.Id), now);
        }
    }
}

/// <summary>
/// A declined payment closes the checkout at once instead of at its deadline: the same steps,
/// payment cancelled first and seats released second (<see cref="CheckoutDeadlineHandler"/>).
/// </summary>
internal sealed class CloseCheckoutOnPaymentFailed(IOrderRepository orders, IMessageScheduler scheduler, TimeProvider time)
    : IIntegrationEventHandler<PaymentFailed>
{
    public async Task HandleAsync(PaymentFailed integrationEvent, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(integrationEvent.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment failed for unknown order {integrationEvent.OrderId}.");

        if (order.Status == OrderStatus.Pending)
        {
            scheduler.Schedule(new CheckoutDeadline(order.Id, CheckoutCloseReason.PaymentFailed), time.GetUtcNow());
        }
    }
}
