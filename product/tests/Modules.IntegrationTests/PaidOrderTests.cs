using System.Net;
using EventTicketing.Orders.Application.Checkout;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1a O2 (with P2): a paid order is confirmed once; a paid order that can't get its seats is
/// refunded and ends Refunded; the return page's status endpoint.
/// </summary>
public sealed class PaidOrderTests(TestSystem system)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_paid_order_is_confirmed_once_and_its_seats_sold_to_it()
    {
        var (sale, ana, checkout) = await CheckedOutAsync();
        var payment = Paid(checkout.OrderId);
        var webhook = FakeLocalPay.Webhook("payment.succeeded", payment);

        await system.PostWebhookAsync(webhook);
        await system.PostWebhookAsync(webhook);
        await system.DeliverMessagesAsync();

        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Confirmed");
        (await PublishedAsync("orders.order-confirmed", checkout.OrderId)).Should().Be(1);

        // ADR-0007: the event carries what its consumers need, so none of them calls back to Orders.
        (await PayloadFieldAsync("orders.order-confirmed", checkout.OrderId, "customerEmail")).Should().MatchRegex(@"^fan-.+@example\.test$");
        (await PayloadFieldAsync("orders.order-confirmed", checkout.OrderId, "eventName")).Should().Be("Test concert");
        (await system.QueryAsync<Guid>("SELECT seat_id FROM inventory.seats WHERE order_id = @p0 AND status = 'Sold'", checkout.OrderId))
            .Should().BeEquivalentTo(sale.Seats.Take(2));
    }

    [Fact]
    public async Task A_payment_whose_seats_were_lost_is_refunded_and_the_order_ends_refunded()
    {
        var (sale, ana, checkout) = await CheckedOutAsync();
        var payment = Paid(checkout.OrderId);

        // The hold ran past its backstop before the payment was confirmed, and Ben took a seat.
        await system.ExpireHoldAsync(await HoldOfAsync(checkout.OrderId));
        using var ben = system.PublicApi(Callers.Fan());
        await (await ben.HoldAsync(sale.EventId, sale.Seats[1])).PlacedAsync();

        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();

        var order = await ana.OrderAsync(checkout.OrderId);
        (order.Status, order.RefundReason).Should().Be(("Refunded", "SeatsLost"));
        system.LocalPay.RefundsOf(payment.Id).Should().ContainSingle().Which.AmountMinorUnits.Should().Be(5000);
        (await PublishedAsync("orders.order-refunded", checkout.OrderId)).Should().Be(1);
        (await PayloadFieldAsync("orders.order-refunded", checkout.OrderId, "eventName")).Should().Be("Test concert");
        (await PublishedAsync("orders.order-confirmed", checkout.OrderId)).Should().Be(0);
        (await system.ScalarAsync<long>("SELECT count(*) FROM inventory.seats WHERE order_id = @p0", checkout.OrderId)).Should().Be(0);
    }

    [Fact]
    public async Task Money_that_arrives_after_the_checkout_closed_is_refunded()
    {
        var (_, ana, checkout) = await CheckedOutAsync();
        (await system.MakeDueAsync("orders", typeof(CheckoutDeadline), checkout.OrderId)).Should().Be(1);
        await system.DeliverMessagesAsync();
        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Expired");

        // A provider that took the money anyway (the race ADR-0010 makes rare, not impossible).
        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "succeeded");
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();

        var order = await ana.OrderAsync(checkout.OrderId);
        (order.Status, order.RefundReason).Should().Be(("Refunded", "PaidAfterClose"));
        system.LocalPay.RefundsOf(payment.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task A_refund_asked_while_LocalPay_is_down_is_asked_again_later()
    {
        var (sale, ana, checkout) = await CheckedOutAsync();
        var payment = Paid(checkout.OrderId);
        await system.ExpireHoldAsync(await HoldOfAsync(checkout.OrderId));
        using var ben = system.PublicApi(Callers.Fan());
        await (await ben.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync();

        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        system.LocalPay.MakeUnreachable(payment.Id);
        await system.DeliverMessagesAsync();
        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Refunding");

        system.LocalPay.MakeReachable(payment.Id);
        (await system.MakeDueAsync("orders", typeof(RefundOrder), checkout.OrderId)).Should().Be(1);
        await system.DeliverMessagesAsync();

        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Refunded");
        system.LocalPay.RefundsOf(payment.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task A_refund_LocalPay_refuses_leaves_the_order_for_a_person()
    {
        var (sale, ana, checkout) = await CheckedOutAsync();
        var payment = Paid(checkout.OrderId);
        system.LocalPay.RefuseRefunds(payment.Id);
        await system.ExpireHoldAsync(await HoldOfAsync(checkout.OrderId));
        using var ben = system.PublicApi(Callers.Fan());
        await (await ben.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync();

        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();

        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("RefundFailed");
    }

    [Fact]
    public async Task The_return_page_polls_an_uncached_status_only_its_fan_can_see()
    {
        var (sale, ana, checkout) = await CheckedOutAsync();

        var response = await ana.GetAsync(new Uri($"/orders/{checkout.OrderId}", UriKind.Relative), Ct);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        var order = await ana.OrderAsync(checkout.OrderId);
        order.EventId.Should().Be(sale.EventId);
        order.PaymentDeadline.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(1));
        order.Seats.Select(s => s.SeatId).Should().BeEquivalentTo(sale.Seats.Take(2));
        order.Seats.Should().OnlyContain(s => s.PriceMinorUnits == 2500);

        using var ben = system.PublicApi(Callers.Fan());
        (await ben.GetAsync(new Uri($"/orders/{checkout.OrderId}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private FakePayment Paid(Guid orderId)
    {
        var payment = system.LocalPay.PaymentOf(orderId);
        system.LocalPay.Settle(payment, "succeeded");
        return payment;
    }

    private Task<Guid> HoldOfAsync(Guid orderId) => system.ScalarAsync<Guid>("SELECT hold_id FROM orders.orders WHERE id = @p0", orderId);

    private async Task<(Sale Sale, HttpClient Fan, CheckoutStarted Checkout)> CheckedOutAsync()
    {
        var sale = await system.OnSaleAsync();
        var fan = system.PublicApi(Callers.Fan());
        var hold = await (await fan.HoldAsync(sale.EventId, sale.Seats[0], sale.Seats[1])).PlacedAsync();
        return (sale, fan, await fan.CheckoutAsync(hold.HoldId));
    }

    private Task<long> PublishedAsync(string type, Guid orderId) =>
        system.ScalarAsync<long>("SELECT count(*) FROM orders.outbox_messages WHERE type = @p0 AND payload->>'orderId' = @p1", type, orderId.ToString());

    private Task<string> PayloadFieldAsync(string type, Guid orderId, string field) =>
        system.ScalarAsync<string>("SELECT payload->>@p2 FROM orders.outbox_messages WHERE type = @p0 AND payload->>'orderId' = @p1", type, orderId.ToString(), field);
}
