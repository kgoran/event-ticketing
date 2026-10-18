using EventTicketing.Payments.Domain;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Payments.Application.Checkout;

public enum CheckoutCancellation
{
    /// <summary>Nothing can be paid any more: the seats can be released.</summary>
    Cancelled,

    /// <summary>The payment went through first: wait for PaymentSucceeded.</summary>
    AlreadyCompleted,
}

/// <summary>
/// Cancels an order's payment at the provider, at the checkout deadline (ADR-0010: payment first,
/// seats second). A coordinator: the provider call runs outside any transaction. Safe to repeat.
/// Called through IPaymentsApi only.
/// </summary>
public sealed record CancelCheckout(Guid OrderId) : ICommand<CheckoutCancellation>, INonTransactionalCommand;

internal sealed class CancelCheckoutAuthorizer : InternalCommandAuthorizer<CancelCheckout>;

internal sealed class CancelCheckoutHandler(
    IPaymentAttemptRepository attempts,
    IEnumerable<IPaymentProvider> providers,
    ICommandHandler<RecordProviderStatus, Unit> recordStatus)
    : ICommandHandler<CancelCheckout, CheckoutCancellation>
{
    public async Task<Result<CheckoutCancellation>> HandleAsync(CancelCheckout command, CancellationToken cancellationToken)
    {
        var attempt = await attempts.FindByOrderAsync(command.OrderId, cancellationToken);

        // No session stored means the fan never got a payment page: nothing can be paid.
        if (attempt?.ProviderPaymentId is not { } providerPaymentId)
        {
            return CheckoutCancellation.Cancelled;
        }

        switch (attempt.Status)
        {
            case PaymentStatus.Succeeded:
                return CheckoutCancellation.AlreadyCompleted;
            case PaymentStatus.Failed or PaymentStatus.Cancelled:
                return CheckoutCancellation.Cancelled;
        }

        CancelPaymentResult cancelled;
        try
        {
            cancelled = await providers.Single(p => p.Name == attempt.Provider).CancelPaymentAsync(providerPaymentId, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return CheckoutErrors.ProviderUnavailable;
        }

        if (cancelled == CancelPaymentResult.AlreadyCompleted)
        {
            return CheckoutCancellation.AlreadyCompleted;
        }

        var recorded = await recordStatus.HandleAsync(new RecordProviderStatus(attempt.Id, ProviderPaymentStatus.Expired), cancellationToken);
        return recorded.IsSuccess ? CheckoutCancellation.Cancelled : recorded.Error;
    }
}

/// <summary>Applies what the provider said about a payment, in Payments' transaction (see <see cref="PaymentOutcomes"/>).</summary>
public sealed record RecordProviderStatus(Guid AttemptId, ProviderPaymentStatus Status) : ICommand<Unit>;

internal sealed class RecordProviderStatusAuthorizer : InternalCommandAuthorizer<RecordProviderStatus>;

internal sealed class RecordProviderStatusHandler(IPaymentAttemptRepository attempts, PaymentOutcomes outcomes)
    : ICommandHandler<RecordProviderStatus, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RecordProviderStatus command, CancellationToken cancellationToken)
    {
        var attempt = await attempts.FindAsync(command.AttemptId, cancellationToken);
        if (attempt is null)
        {
            return Error.NotFound("payments.attempt_not_found", "The payment attempt does not exist.");
        }

        outcomes.Apply(attempt, command.Status);
        return Unit.Value;
    }
}
