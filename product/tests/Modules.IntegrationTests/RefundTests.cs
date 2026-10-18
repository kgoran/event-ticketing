using EventTicketing.Payments.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>M1a P2: a single full refund through LocalPay, with RefundSucceeded and RefundFailed.</summary>
public sealed class RefundTests(TestSystem system)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_paid_order_is_refunded_once_however_often_it_is_asked()
    {
        var (orderId, payment) = await PaidAsync();

        (await RefundAsync(orderId)).Outcome.Should().Be(RefundRequestOutcome.Requested);
        (await RefundAsync(orderId)).Outcome.Should().Be(RefundRequestOutcome.Requested);

        system.LocalPay.RefundsOf(payment.Id).Should().ContainSingle().Which.AmountMinorUnits.Should().Be(2500);
        (await PublishedAsync("payments.refund-succeeded", orderId)).Should().Be(1);
        (await system.ScalarAsync<string>("SELECT status FROM payments.refunds WHERE order_id = @p0", orderId)).Should().Be("Succeeded");

        // LocalPay's webhook for the same refund changes nothing.
        await system.PostWebhookAsync(FakeLocalPay.RefundWebhook(system.LocalPay.RefundsOf(payment.Id)[0], payment));
        await system.DeliverMessagesAsync();
        (await PublishedAsync("payments.refund-succeeded", orderId)).Should().Be(1);
    }

    [Fact]
    public async Task An_unpaid_order_has_nothing_to_refund()
    {
        var orderId = Guid.NewGuid();
        await PaymentsAsync(api => api.CreateCheckoutAsync(new CreateCheckoutRequest(orderId, 2500, "EUR", "http://shop.test/return"), Ct));

        (await RefundAsync(orderId)).Outcome.Should().Be(RefundRequestOutcome.NotRefundable);
        system.LocalPay.RefundsOf(system.LocalPay.PaymentOf(orderId).Id).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refund_asked_while_LocalPay_is_down_is_made_once_when_asked_again()
    {
        var (orderId, payment) = await PaidAsync();
        system.LocalPay.MakeUnreachable(payment.Id);

        (await RefundAsync(orderId)).Outcome.Should().Be(RefundRequestOutcome.ProviderUnavailable);
        (await system.ScalarAsync<string>("SELECT status FROM payments.refunds WHERE order_id = @p0", orderId)).Should().Be("Requested");

        system.LocalPay.MakeReachable(payment.Id);
        (await RefundAsync(orderId)).Outcome.Should().Be(RefundRequestOutcome.Requested);

        system.LocalPay.RefundsOf(payment.Id).Should().ContainSingle();
        (await PublishedAsync("payments.refund-succeeded", orderId)).Should().Be(1);
    }

    [Fact]
    public async Task A_refund_LocalPay_refuses_publishes_RefundFailed()
    {
        var (orderId, payment) = await PaidAsync();
        system.LocalPay.RefuseRefunds(payment.Id);

        (await RefundAsync(orderId)).Outcome.Should().Be(RefundRequestOutcome.Requested);

        (await PublishedAsync("payments.refund-failed", orderId)).Should().Be(1);
        (await system.ScalarAsync<string>("SELECT failure_reason FROM payments.refunds WHERE order_id = @p0", orderId)).Should().Contain("can't be refunded");
    }

    [Fact]
    public async Task A_refund_webhook_that_overtakes_the_providers_answer_completes_the_refund()
    {
        var (orderId, payment) = await PaidAsync();

        // Recorded but not yet answered: as if the webhook arrived before LocalPay's API response.
        await system.InWorkersAsync(async services =>
        {
            var record = services.GetRequiredService<EventTicketing.Shared.Application.Commands.ICommandHandler<
                EventTicketing.Payments.Application.Refunds.RecordRefund, EventTicketing.Payments.Application.Refunds.RecordedRefund?>>();
            return await record.HandleAsync(new EventTicketing.Payments.Application.Refunds.RecordRefund(orderId, "seats lost"), Ct);
        });

        await system.PostWebhookAsync(FakeLocalPay.RefundWebhook(new FakeRefund("re_early", payment.Id, 2500), payment));
        await system.DeliverMessagesAsync();

        (await system.ScalarAsync<string>("SELECT status FROM payments.refunds WHERE order_id = @p0", orderId)).Should().Be("Succeeded");
        (await PublishedAsync("payments.refund-succeeded", orderId)).Should().Be(1);
    }

    private async Task<(Guid OrderId, FakePayment Payment)> PaidAsync()
    {
        var orderId = Guid.NewGuid();
        await PaymentsAsync(api => api.CreateCheckoutAsync(new CreateCheckoutRequest(orderId, 2500, "EUR", "http://shop.test/return"), Ct));
        var payment = system.LocalPay.PaymentOf(orderId);
        system.LocalPay.Settle(payment, "succeeded");
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();
        return (orderId, payment);
    }

    private Task<RefundRequestResult> RefundAsync(Guid orderId) => PaymentsAsync(api => api.RequestRefundAsync(orderId, "seats lost", Ct));

    private Task<T> PaymentsAsync<T>(Func<IPaymentsApi, Task<T>> call) =>
        system.InWorkersAsync(services => call(services.GetRequiredService<IPaymentsApi>()));

    private Task<long> PublishedAsync(string type, Guid orderId) =>
        system.ScalarAsync<long>("SELECT count(*) FROM payments.outbox_messages WHERE type = @p0 AND payload->>'orderId' = @p1", type, orderId.ToString());
}
