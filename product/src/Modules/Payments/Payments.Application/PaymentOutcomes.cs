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
        ArgumentNullException.ThrowIfNull(attempt);

        var now = time.GetUtcNow();
        switch (status)
        {
            case ProviderPaymentStatus.Succeeded when attempt.Succeed(now):
                // The event joins the checkout's trace, so Orders' confirmation shows up in the same trace.
                using (PaymentsTelemetry.ContinueCheckout(attempt.CheckoutTraceParent, "payment succeeded"))
                {
                    events.Publish(new PaymentSucceeded(Guid.CreateVersion7(), now, attempt.OrderId, attempt.Id, attempt.AmountMinorUnits, attempt.Currency));
                }

                Record("succeeded");
                break;
            case ProviderPaymentStatus.Failed when attempt.Fail(now):
                using (PaymentsTelemetry.ContinueCheckout(attempt.CheckoutTraceParent, "payment failed"))
                {
                    events.Publish(new PaymentFailed(Guid.CreateVersion7(), now, attempt.OrderId, attempt.Id));
                }

                Record("failed");
                break;
            case ProviderPaymentStatus.Expired when attempt.Status is not (PaymentStatus.Cancelled or PaymentStatus.Succeeded or PaymentStatus.Failed):
                attempt.Cancel(now);
                Record("cancelled");
                break;
        }
    }

    private static void Record(string status) =>
        PaymentsTelemetry.Outcomes.Add(1, new KeyValuePair<string, object?>("eventticketing.payment.status", status));
}
