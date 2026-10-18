using EventTicketing.Inventory.Contracts;
using EventTicketing.Orders.Contracts;
using EventTicketing.Orders.Domain;
using EventTicketing.Payments.Contracts;
using EventTicketing.Shared.Application.Messaging;

namespace EventTicketing.Orders.Application.Checkout;

/// <summary>
/// After the provider confirmed the payment, sell the hold's seats to the order (ADR-0009). Inventory
/// commits on its own first; if this transaction then fails, the message is redelivered and the
/// confirmation is simply repeated, which Inventory allows for the same order.
/// </summary>
internal sealed class ConfirmOrderOnPaymentSucceeded(
    IOrderRepository orders,
    IInventoryApi inventory,
    IIntegrationEventPublisher events,
    TimeProvider time)
    : IIntegrationEventHandler<PaymentSucceeded>
{
    public async Task HandleAsync(PaymentSucceeded integrationEvent, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(integrationEvent.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment succeeded for unknown order {integrationEvent.OrderId}.");

        if (order.Status != OrderStatus.Pending)
        {
            return;
        }

        var confirmation = await inventory.ConfirmHoldAsync(order.HoldId, order.Id, cancellationToken);
        if (confirmation.Outcome == ConfirmHoldOutcome.SeatsLost)
        {
            order.MarkSeatsLost(); // refunding it arrives with backlog item P2
            return;
        }

        if (order.Confirm())
        {
            events.Publish(new OrderConfirmed(
                Guid.CreateVersion7(),
                time.GetUtcNow(),
                order.Id,
                order.CustomerId,
                order.EventId,
                order.TotalMinorUnits,
                order.Currency,
                [.. order.Lines.Select(line => new ConfirmedSeat(line.SeatId, line.PriceCategoryId, line.PriceMinorUnits))]));
        }
    }
}
