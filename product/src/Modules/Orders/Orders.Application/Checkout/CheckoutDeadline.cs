using EventTicketing.Inventory.Contracts;
using EventTicketing.Orders.Domain;
using EventTicketing.Payments.Contracts;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Scheduling;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Orders.Application.Checkout;

/// <summary>
/// Close a Pending order's checkout without a sale: at its deadline, or when its payment failed
/// (scheduled due now). Scheduled when the order is created.
/// </summary>
public sealed record CheckoutDeadline(Guid OrderId, CheckoutCloseReason Reason = CheckoutCloseReason.Deadline);

/// <summary>The provider said the payment completed, but no webhook has confirmed it yet: ask the provider.</summary>
public sealed record CheckOrderPayment(Guid OrderId);

/// <summary>
/// Closing a checkout, in the order ADR-0010 requires: cancel the payment first, release the seats
/// second, so the normal path never takes money for seats it gave away. A coordinator: each step is
/// another module's API or an Orders command with its own transaction, and all are safe to repeat.
/// </summary>
internal sealed class CheckoutDeadlineHandler(
    IOrderRepository orders,
    IPaymentsApi payments,
    IInventoryApi inventory,
    ICommandHandler<CloseUnsoldOrder, Unit> closeOrder,
    ICommandHandler<ScheduleCheckoutFollowUp, Unit> followUp,
    CheckoutPolicy policy,
    TimeProvider time)
    : IScheduledMessageHandler<CheckoutDeadline>, INonTransactionalMessageHandler
{
    public async Task HandleAsync(CheckoutDeadline message, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(message.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order {message.OrderId} does not exist.");

        if (order.Status != OrderStatus.Pending)
        {
            return; // paid or closed meanwhile
        }

        var cancelled = await payments.CancelCheckoutAsync(order.Id, cancellationToken);
        switch (cancelled.Outcome)
        {
            case CancelCheckoutOutcome.Cancelled:
                // No payment can arrive any more, so the seats can go back.
                var released = await inventory.ReleaseHoldAsync(order.HoldId, order.Id, cancellationToken);
                if (released.Outcome == ReleaseHoldOutcome.Released)
                {
                    Ensure(await closeOrder.HandleAsync(new CloseUnsoldOrder(order.Id, message.Reason), cancellationToken));
                }

                break;

            case CancelCheckoutOutcome.AlreadyCompleted:
                // The payment won the race: its webhook confirms the order. If it doesn't come, ask.
                Ensure(await followUp.HandleAsync(
                    ScheduleCheckoutFollowUp.CheckPayment(order.Id, time.GetUtcNow() + policy.WebhookGracePeriod),
                    cancellationToken));
                break;

            case CancelCheckoutOutcome.ProviderUnavailable:
                // Try again; at worst the seats come back at Inventory's backstop (ADR-0010).
                Ensure(await followUp.HandleAsync(
                    ScheduleCheckoutFollowUp.CloseAgain(order.Id, message.Reason, time.GetUtcNow() + policy.ProviderRetryDelay),
                    cancellationToken));
                break;
        }
    }

    private static void Ensure(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Closing the checkout failed: {result.Error.Code}.");
        }
    }
}

/// <summary>
/// Asks the provider about a payment it said had completed, when no webhook has arrived since. A
/// success found this way publishes PaymentSucceeded (Payments), which confirms the order as usual.
/// </summary>
internal sealed class CheckOrderPaymentHandler(
    IOrderRepository orders,
    IPaymentsApi payments,
    ICommandHandler<ScheduleCheckoutFollowUp, Unit> followUp,
    CheckoutPolicy policy,
    TimeProvider time)
    : IScheduledMessageHandler<CheckOrderPayment>, INonTransactionalMessageHandler
{
    public async Task HandleAsync(CheckOrderPayment message, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(message.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order {message.OrderId} does not exist.");

        if (order.Status != OrderStatus.Pending)
        {
            return; // the webhook came after all
        }

        var status = await payments.CheckPaymentStatusAsync(order.Id, cancellationToken);
        var next = status.Outcome switch
        {
            PaymentStatusOutcome.Succeeded => null, // PaymentSucceeded is on its way
            PaymentStatusOutcome.Failed => ScheduleCheckoutFollowUp.CloseAgain(order.Id, CheckoutCloseReason.PaymentFailed, time.GetUtcNow()),
            PaymentStatusOutcome.Cancelled => ScheduleCheckoutFollowUp.CloseAgain(order.Id, CheckoutCloseReason.Deadline, time.GetUtcNow()),
            _ => ScheduleCheckoutFollowUp.CheckPayment(order.Id, time.GetUtcNow() + policy.ProviderRetryDelay),
        };

        if (next is not null)
        {
            var scheduled = await followUp.HandleAsync(next, cancellationToken);
            if (scheduled.IsFailure)
            {
                throw new InvalidOperationException($"Scheduling the next step failed: {scheduled.Error.Code}.");
            }
        }
    }
}

/// <summary>Marks a Pending order Expired or Failed, once its payment is cancelled and its seats released.</summary>
public sealed record CloseUnsoldOrder(Guid OrderId, CheckoutCloseReason Reason) : ICommand<Unit>;

internal sealed class CloseUnsoldOrderAuthorizer : InternalCommandAuthorizer<CloseUnsoldOrder>;

internal sealed class CloseUnsoldOrderHandler(IOrderRepository orders, TimeProvider time) : ICommandHandler<CloseUnsoldOrder, Unit>
{
    public async Task<Result<Unit>> HandleAsync(CloseUnsoldOrder command, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(command.OrderId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("orders.not_found", "The order does not exist.");
        }

        if (order.CloseUnsold(command.Reason, time.GetUtcNow()))
        {
            OrdersTelemetry.Moved(order.Status);
        }

        return Unit.Value;
    }
}

/// <summary>Schedules the checkout's next step (a scheduled message needs an Orders transaction).</summary>
public sealed record ScheduleCheckoutFollowUp(Guid OrderId, bool CheckPaymentOnly, CheckoutCloseReason Reason, DateTimeOffset DueAt) : ICommand<Unit>
{
    public static ScheduleCheckoutFollowUp CheckPayment(Guid orderId, DateTimeOffset dueAt) => new(orderId, true, CheckoutCloseReason.Deadline, dueAt);

    public static ScheduleCheckoutFollowUp CloseAgain(Guid orderId, CheckoutCloseReason reason, DateTimeOffset dueAt) => new(orderId, false, reason, dueAt);
}

internal sealed class ScheduleCheckoutFollowUpAuthorizer : InternalCommandAuthorizer<ScheduleCheckoutFollowUp>;

internal sealed class ScheduleCheckoutFollowUpHandler(IMessageScheduler scheduler) : ICommandHandler<ScheduleCheckoutFollowUp, Unit>
{
    public Task<Result<Unit>> HandleAsync(ScheduleCheckoutFollowUp command, CancellationToken cancellationToken)
    {
        if (command.CheckPaymentOnly)
        {
            scheduler.Schedule(new CheckOrderPayment(command.OrderId), command.DueAt);
        }
        else
        {
            scheduler.Schedule(new CheckoutDeadline(command.OrderId, command.Reason), command.DueAt);
        }

        return Task.FromResult<Result<Unit>>(Unit.Value);
    }
}
