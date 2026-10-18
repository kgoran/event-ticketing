using EventTicketing.Orders.Contracts;
using EventTicketing.Orders.Domain;
using EventTicketing.Payments.Contracts;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Application.Scheduling;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Orders.Application.Checkout;

/// <summary>Ask Payments to refund a Refunding order. Scheduled when the order starts refunding.</summary>
public sealed record RefundOrder(Guid OrderId);

/// <summary>
/// Asks Payments for the order's refund, outside any transaction (a provider call is involved). Safe
/// to repeat: Payments makes one refund per payment. RefundSucceeded or RefundFailed follows.
/// </summary>
internal sealed class RefundOrderHandler(
    IOrderRepository orders,
    IPaymentsApi payments,
    ICommandHandler<ScheduleRefundRetry, Unit> retry,
    CheckoutPolicy policy,
    TimeProvider time)
    : IScheduledMessageHandler<RefundOrder>, INonTransactionalMessageHandler
{
    public async Task HandleAsync(RefundOrder message, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(message.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order {message.OrderId} does not exist.");

        if (order.Status != OrderStatus.Refunding)
        {
            return;
        }

        var requested = await payments.RequestRefundAsync(order.Id, order.RefundReason?.ToString() ?? "refund", cancellationToken);
        switch (requested.Outcome)
        {
            case RefundRequestOutcome.ProviderUnavailable:
                var scheduled = await retry.HandleAsync(new ScheduleRefundRetry(order.Id, time.GetUtcNow() + policy.ProviderRetryDelay), cancellationToken);
                if (scheduled.IsFailure)
                {
                    throw new InvalidOperationException($"Scheduling the refund retry failed: {scheduled.Error.Code}.");
                }

                break;

            case RefundRequestOutcome.NotRefundable:
                // The payment succeeded, or the order wouldn't be refunding: a bug, so let it dead-letter.
                throw new InvalidOperationException($"Payments says order {order.Id} has nothing to refund.");
        }
    }
}

/// <summary>Schedules another refund attempt (a scheduled message needs an Orders transaction).</summary>
public sealed record ScheduleRefundRetry(Guid OrderId, DateTimeOffset DueAt) : ICommand<Unit>;

internal sealed class ScheduleRefundRetryAuthorizer : InternalCommandAuthorizer<ScheduleRefundRetry>;

internal sealed class ScheduleRefundRetryHandler(IMessageScheduler scheduler) : ICommandHandler<ScheduleRefundRetry, Unit>
{
    public Task<Result<Unit>> HandleAsync(ScheduleRefundRetry command, CancellationToken cancellationToken)
    {
        scheduler.Schedule(new RefundOrder(command.OrderId), command.DueAt);
        return Task.FromResult<Result<Unit>>(Unit.Value);
    }
}

/// <summary>The money is back with the fan.</summary>
internal sealed class CompleteRefundOnRefundSucceeded(IOrderRepository orders, IIntegrationEventPublisher events, TimeProvider time)
    : IIntegrationEventHandler<RefundSucceeded>
{
    public async Task HandleAsync(RefundSucceeded integrationEvent, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(integrationEvent.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Refund succeeded for unknown order {integrationEvent.OrderId}.");

        if (order.CompleteRefund())
        {
            OrdersTelemetry.Moved(OrderStatus.Refunded);
            events.Publish(new OrderRefunded(
                Guid.CreateVersion7(),
                time.GetUtcNow(),
                order.Id,
                order.CustomerId,
                order.EventId,
                integrationEvent.AmountMinorUnits,
                integrationEvent.Currency,
                order.RefundReason?.ToString() ?? "refund"));
        }
    }
}

/// <summary>The provider refused the refund; the order says so until a person sorts it out.</summary>
internal sealed class FailRefundOnRefundFailed(IOrderRepository orders) : IIntegrationEventHandler<RefundFailed>
{
    public async Task HandleAsync(RefundFailed integrationEvent, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(integrationEvent.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Refund failed for unknown order {integrationEvent.OrderId}.");

        if (order.FailRefund())
        {
            OrdersTelemetry.Moved(OrderStatus.RefundFailed);
        }
    }
}
