using EventTicketing.Payments.Application.Checkout;
using EventTicketing.Payments.Contracts;
using EventTicketing.Payments.Domain;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Payments.Application.Refunds;

/// <summary>
/// Refunds an order's payment in full (P2: the "seats lost" case). A coordinator: the refund is
/// recorded first, the provider is asked outside any transaction with the refund's ID as idempotency
/// key, and its answer recorded after. Safe to repeat: a second request reuses the first refund and
/// asks the provider again only while it is still Requested. Called through IPaymentsApi only.
/// </summary>
public sealed record RequestRefund(Guid OrderId, string Reason) : ICommand<RefundRequestOutcome>, INonTransactionalCommand;

internal sealed class RequestRefundAuthorizer : InternalCommandAuthorizer<RequestRefund>;

internal sealed class RequestRefundHandler(
    ICommandHandler<RecordRefund, RecordedRefund?> recordRefund,
    ICommandHandler<RecordRefundOutcome, Unit> recordOutcome,
    IEnumerable<IPaymentProvider> providers)
    : ICommandHandler<RequestRefund, RefundRequestOutcome>
{
    public async Task<Result<RefundRequestOutcome>> HandleAsync(RequestRefund command, CancellationToken cancellationToken)
    {
        var recorded = await recordRefund.HandleAsync(new RecordRefund(command.OrderId, command.Reason), cancellationToken);
        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (recorded.Value is not { } refund)
        {
            return RefundRequestOutcome.NotRefundable;
        }

        if (refund.Status != RefundStatus.Requested)
        {
            return RefundRequestOutcome.Requested; // already answered
        }

        RefundResult result;
        try
        {
            result = await providers.Single(p => p.Name == refund.Provider).RefundAsync(
                new RefundRequest(refund.RefundId, refund.ProviderPaymentId, refund.AmountMinorUnits, refund.Currency, command.Reason),
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return CheckoutErrors.ProviderUnavailable;
        }

        var outcome = await recordOutcome.HandleAsync(new RecordRefundOutcome(refund.RefundId, result), cancellationToken);
        return outcome.IsSuccess ? RefundRequestOutcome.Requested : outcome.Error;
    }
}

/// <summary>Records the order's refund, or returns the existing one; null if the payment never succeeded.</summary>
public sealed record RecordRefund(Guid OrderId, string Reason) : ICommand<RecordedRefund?>;

public sealed record RecordedRefund(Guid RefundId, RefundStatus Status, string Provider, string ProviderPaymentId, long AmountMinorUnits, string Currency);

internal sealed class RecordRefundAuthorizer : InternalCommandAuthorizer<RecordRefund>;

internal sealed class RecordRefundHandler(IPaymentAttemptRepository attempts, IRefundRepository refunds, TimeProvider time)
    : ICommandHandler<RecordRefund, RecordedRefund?>
{
    public async Task<Result<RecordedRefund?>> HandleAsync(RecordRefund command, CancellationToken cancellationToken)
    {
        var attempt = await attempts.FindByOrderAsync(command.OrderId, cancellationToken);
        if (attempt is not { Status: PaymentStatus.Succeeded, ProviderPaymentId: { } providerPaymentId })
        {
            return Result.Success<RecordedRefund?>(null);
        }

        var refund = await refunds.FindByAttemptAsync(attempt.Id, cancellationToken);
        if (refund is null)
        {
            refund = Refund.For(attempt, command.Reason, time.GetUtcNow());
            refunds.Add(refund);
        }

        return new RecordedRefund(refund.Id, refund.Status, attempt.Provider, providerPaymentId, refund.AmountMinorUnits, refund.Currency);
    }
}

/// <summary>Applies the provider's answer to a refund; the first answer that changes it publishes the event.</summary>
public sealed record RecordRefundOutcome(Guid RefundId, RefundResult Result) : ICommand<Unit>;

internal sealed class RecordRefundOutcomeAuthorizer : InternalCommandAuthorizer<RecordRefundOutcome>;

internal sealed class RecordRefundOutcomeHandler(IRefundRepository refunds, RefundOutcomes outcomes)
    : ICommandHandler<RecordRefundOutcome, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RecordRefundOutcome command, CancellationToken cancellationToken)
    {
        var refund = await refunds.FindAsync(command.RefundId, cancellationToken);
        if (refund is null)
        {
            return Error.NotFound("payments.refund_not_found", "The refund does not exist.");
        }

        outcomes.Apply(refund, command.Result);
        return Unit.Value;
    }
}

/// <summary>What the provider said about a refund, from its API answer or a webhook, applied once.</summary>
public sealed class RefundOutcomes(IIntegrationEventPublisher events, TimeProvider time)
{
    public void Apply(Refund refund, RefundResult result)
    {
        ArgumentNullException.ThrowIfNull(refund);
        ArgumentNullException.ThrowIfNull(result);

        var now = time.GetUtcNow();
        switch (result.Status)
        {
            case ProviderRefundStatus.Succeeded when refund.Succeed(result.ProviderRefundId, now):
                events.Publish(new RefundSucceeded(Guid.CreateVersion7(), now, refund.OrderId, refund.Id, refund.AmountMinorUnits, refund.Currency));
                break;
            case ProviderRefundStatus.Failed when refund.Fail(result.FailureReason ?? "The provider refused the refund.", now):
                events.Publish(new RefundFailed(Guid.CreateVersion7(), now, refund.OrderId, refund.Id, refund.FailureReason!));
                break;
        }
    }
}
