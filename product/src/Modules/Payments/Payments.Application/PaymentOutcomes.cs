using EventTicketing.Payments.Contracts;
using EventTicketing.Payments.Domain;
using EventTicketing.Shared.Application.Messaging;

namespace EventTicketing.Payments.Application;

/// <summary>
/// What the provider says happened to a payment, from a webhook or a status check, applied once:
/// the first report that changes the attempt publishes the event, repeats publish nothing.
/// </summary>
public sealed class PaymentOutcomes(IIntegrationEventPublisher events, TimeProvider time)
{
    public void Apply(PaymentAttempt attempt, ProviderPaymentStatus status)
    {
        var now = time.GetUtcNow();
        switch (status)
        {
            case ProviderPaymentStatus.Succeeded when attempt.Succeed(now):
                events.Publish(new PaymentSucceeded(Guid.CreateVersion7(), now, attempt.OrderId, attempt.Id, attempt.AmountMinorUnits, attempt.Currency));
                break;
            case ProviderPaymentStatus.Failed when attempt.Fail(now):
                events.Publish(new PaymentFailed(Guid.CreateVersion7(), now, attempt.OrderId, attempt.Id));
                break;
            case ProviderPaymentStatus.Expired:
                attempt.Cancel(now);
                break;
        }
    }
}
