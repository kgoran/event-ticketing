using EventTicketing.Payments.Domain;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Payments.Application.Checkout;

/// <summary>
/// Asks the provider about an order's payment when its webhook is late or missing (docs/payments.md).
/// What it learns is applied like a webhook: a success publishes PaymentSucceeded once. A
/// coordinator, so the provider call runs outside any transaction. Called through IPaymentsApi only.
/// </summary>
public sealed record CheckPaymentStatus(Guid OrderId) : ICommand<ProviderPaymentStatus>, INonTransactionalCommand;

internal sealed class CheckPaymentStatusAuthorizer : InternalCommandAuthorizer<CheckPaymentStatus>;

internal sealed class CheckPaymentStatusHandler(
    IPaymentAttemptRepository attempts,
    IEnumerable<IPaymentProvider> providers,
    ICommandHandler<RecordProviderStatus, Unit> recordStatus)
    : ICommandHandler<CheckPaymentStatus, ProviderPaymentStatus>
{
    public async Task<Result<ProviderPaymentStatus>> HandleAsync(CheckPaymentStatus command, CancellationToken cancellationToken)
    {
        var attempt = await attempts.FindByOrderAsync(command.OrderId, cancellationToken);
        if (attempt?.ProviderPaymentId is not { } providerPaymentId)
        {
            return Error.NotFound("payments.no_payment", "The order has no payment at a provider.");
        }

        ProviderPaymentStatus status;
        try
        {
            status = await providers.Single(p => p.Name == attempt.Provider).GetStatusAsync(providerPaymentId, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return CheckoutErrors.ProviderUnavailable;
        }

        if (status != ProviderPaymentStatus.Open)
        {
            var recorded = await recordStatus.HandleAsync(new RecordProviderStatus(attempt.Id, status), cancellationToken);
            if (recorded.IsFailure)
            {
                return recorded.Error;
            }
        }

        return status;
    }
}
