using System.Net;
using System.Text;
using EventTicketing.Payments.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1a P1: the Payments module API, the LocalPay adapter, and webhooks through WebhookIngress.
/// Duplicate and out-of-order webhooks must leave exactly one outcome (ADR-0009).
/// </summary>
public sealed class PaymentsTests(TestSystem system)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signed_webhook_confirms_the_payment_once_even_when_repeated()
    {
        var (orderId, payment) = await CheckoutAsync(2500);
        var webhook = FakeLocalPay.Webhook("payment.succeeded", payment);

        (await PostAsync(webhook)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(webhook)).StatusCode.Should().Be(HttpStatusCode.OK, "a duplicate is acknowledged so the provider stops retrying");
        await system.DeliverMessagesAsync();

        (await StatusAsync(orderId)).Should().Be("Succeeded");
        (await PublishedAsync("payments.payment-succeeded", orderId)).Should().Be(1);
    }

    [Fact]
    public async Task A_webhook_arriving_twice_at_the_same_moment_is_stored_once()
    {
        var (orderId, payment) = await CheckoutAsync(2500);
        var webhook = FakeLocalPay.Webhook("payment.succeeded", payment);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(webhook)));
        await system.DeliverMessagesAsync();

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
        (await system.ScalarAsync<long>("SELECT count(*) FROM payments.received_webhooks WHERE provider_payment_id = @p0", payment.Id)).Should().Be(1);
        (await PublishedAsync("payments.payment-succeeded", orderId)).Should().Be(1);
    }

    [Fact]
    public async Task Webhooks_with_a_wrong_or_old_signature_are_refused()
    {
        var (_, payment) = await CheckoutAsync(2500);

        (await PostAsync(FakeLocalPay.Webhook("payment.succeeded", payment, secret: "not-the-secret"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync(FakeLocalPay.Webhook("payment.succeeded", payment, signedAt: DateTimeOffset.UtcNow.AddMinutes(-10))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "an old webhook could be a replay");
    }

    [Fact]
    public async Task A_late_failure_never_overrides_a_success()
    {
        var (orderId, payment) = await CheckoutAsync(2500);

        await PostAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();
        await PostAsync(FakeLocalPay.Webhook("payment.failed", payment));
        await system.DeliverMessagesAsync();

        (await StatusAsync(orderId)).Should().Be("Succeeded");
        (await PublishedAsync("payments.payment-failed", orderId)).Should().Be(0);
    }

    [Fact]
    public async Task A_webhook_that_overtakes_the_stored_session_still_finds_its_payment()
    {
        var orderId = Guid.NewGuid();

        // The webhook arrives and is processed while LocalPay is still answering the create call.
        system.LocalPay.WhenCreated(orderId, async payment =>
        {
            (await PostAsync(FakeLocalPay.Webhook("payment.succeeded", payment))).StatusCode.Should().Be(HttpStatusCode.OK);
            await system.DeliverMessagesAsync();
        });

        var created = await PaymentsAsync(api => api.CreateCheckoutAsync(new CreateCheckoutRequest(orderId, 2500, "EUR", "http://shop.test/return"), Ct));

        created.Outcome.Should().Be(CreateCheckoutOutcome.Created);
        (await StatusAsync(orderId)).Should().Be("Succeeded", "storing the session afterwards doesn't undo the outcome");
        (await PublishedAsync("payments.payment-succeeded", orderId)).Should().Be(1);
    }

    [Fact]
    public async Task A_declined_payment_publishes_PaymentFailed()
    {
        var (orderId, payment) = await CheckoutAsync(2500);

        await PostAsync(FakeLocalPay.Webhook("payment.failed", payment));
        await system.DeliverMessagesAsync();

        (await StatusAsync(orderId)).Should().Be("Failed");
        (await PublishedAsync("payments.payment-failed", orderId)).Should().Be(1);
    }

    [Fact]
    public async Task A_webhook_for_another_amount_marks_nothing_paid()
    {
        var (orderId, payment) = await CheckoutAsync(2500);

        await PostAsync(FakeLocalPay.Webhook("payment.succeeded", payment, amountMinorUnits: 1));
        await system.DeliverMessagesAsync();

        (await StatusAsync(orderId)).Should().Be("AwaitingPayment");
        (await PublishedAsync("payments.payment-succeeded", orderId)).Should().Be(0);
        (await system.ScalarAsync<string>(
            "SELECT last_error FROM payments.scheduled_messages WHERE payload->>'webhookId' = " +
            "(SELECT id::text FROM payments.received_webhooks WHERE provider_payment_id = @p0)",
            payment.Id)).Should().Contain("expects 2500");
    }

    [Fact]
    public async Task CancelCheckout_expires_an_open_payment_and_is_safe_to_repeat()
    {
        var (orderId, payment) = await CheckoutAsync(2500);

        (await PaymentsAsync(api => api.CancelCheckoutAsync(orderId, Ct))).Outcome.Should().Be(CancelCheckoutOutcome.Cancelled);
        (await PaymentsAsync(api => api.CancelCheckoutAsync(orderId, Ct))).Outcome.Should().Be(CancelCheckoutOutcome.Cancelled);

        payment.Status.Should().Be("expired");
        (await StatusAsync(orderId)).Should().Be("Cancelled");
    }

    [Fact]
    public async Task CancelCheckout_of_a_payment_that_went_through_says_so()
    {
        var (orderId, payment) = await CheckoutAsync(2500);
        system.LocalPay.Settle(payment, "succeeded");

        (await PaymentsAsync(api => api.CancelCheckoutAsync(orderId, Ct))).Outcome.Should().Be(CancelCheckoutOutcome.AlreadyCompleted);
        (await StatusAsync(orderId)).Should().Be("AwaitingPayment", "the webhook decides, not the cancel call");
    }

    [Fact]
    public async Task CancelCheckout_when_the_provider_is_unreachable_asks_to_try_again()
    {
        var (orderId, payment) = await CheckoutAsync(2500);
        system.LocalPay.MakeUnreachable(payment.Id);

        (await PaymentsAsync(api => api.CancelCheckoutAsync(orderId, Ct))).Outcome.Should().Be(CancelCheckoutOutcome.ProviderUnavailable);
    }

    [Fact]
    public async Task CancelCheckout_of_an_order_that_never_got_a_payment_page_is_cancelled()
    {
        (await PaymentsAsync(api => api.CancelCheckoutAsync(Guid.NewGuid(), Ct))).Outcome.Should().Be(CancelCheckoutOutcome.Cancelled);
    }

    [Fact]
    public async Task CheckPaymentStatus_finds_a_payment_whose_webhook_never_came()
    {
        var (orderId, payment) = await CheckoutAsync(2500);
        (await PaymentsAsync(api => api.CheckPaymentStatusAsync(orderId, Ct))).Outcome.Should().Be(PaymentStatusOutcome.Pending);

        system.LocalPay.Settle(payment, "succeeded");

        (await PaymentsAsync(api => api.CheckPaymentStatusAsync(orderId, Ct))).Outcome.Should().Be(PaymentStatusOutcome.Succeeded);
        (await PaymentsAsync(api => api.CheckPaymentStatusAsync(orderId, Ct))).Outcome.Should().Be(PaymentStatusOutcome.Succeeded);
        (await PublishedAsync("payments.payment-succeeded", orderId)).Should().Be(1);

        // The webhook arriving late after all changes nothing.
        await PostAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();
        (await PublishedAsync("payments.payment-succeeded", orderId)).Should().Be(1);
    }

    private async Task<(Guid OrderId, FakePayment Payment)> CheckoutAsync(long amountMinorUnits)
    {
        var orderId = Guid.NewGuid();
        var created = await PaymentsAsync(api => api.CreateCheckoutAsync(new CreateCheckoutRequest(orderId, amountMinorUnits, "EUR", "http://shop.test/return"), Ct));
        created.Outcome.Should().Be(CreateCheckoutOutcome.Created);
        return (orderId, system.LocalPay.PaymentOf(orderId));
    }

    private Task<T> PaymentsAsync<T>(Func<IPaymentsApi, Task<T>> call) =>
        system.InWorkersAsync(services => call(services.GetRequiredService<IPaymentsApi>()));

    private async Task<HttpResponseMessage> PostAsync((string Body, string Signature) webhook)
    {
        using var ingress = system.WebhookIngress();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/localpay")
        {
            Content = new StringContent(webhook.Body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("LocalPay-Signature", webhook.Signature);
        return await ingress.SendAsync(request, Ct);
    }

    private Task<string> StatusAsync(Guid orderId) =>
        system.ScalarAsync<string>("SELECT status FROM payments.payment_attempts WHERE order_id = @p0", orderId);

    private Task<long> PublishedAsync(string type, Guid orderId) =>
        system.ScalarAsync<long>("SELECT count(*) FROM payments.outbox_messages WHERE type = @p0 AND payload->>'orderId' = @p1", type, orderId.ToString());
}
