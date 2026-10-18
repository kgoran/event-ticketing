using System.Net;
using System.Net.Http.Json;
using EventTicketing.Catalog.Events;
using EventTicketing.Orders.Application.Checkout;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1a O1: checkout creates a priced order with a deadline; at the deadline the payment is
/// cancelled first and the seats released second (ADR-0010), whatever the provider answers.
/// </summary>
public sealed class OrdersTests(TestSystem system)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Checkout_creates_a_pending_order_priced_from_the_price_list_with_a_deadline()
    {
        var sale = await system.OnSaleAsync(priceMinorUnits: 2500);
        using var ana = system.PublicApi(Callers.Fan());
        var hold = await (await ana.HoldAsync(sale.EventId, sale.Seats[0], sale.Seats[1])).PlacedAsync();

        var checkout = await ana.CheckoutAsync(hold.HoldId);

        var order = await ana.OrderAsync(checkout.OrderId);
        (order.Status, order.TotalMinorUnits, order.Currency).Should().Be(("Pending", 5000L, "EUR"));
        checkout.RedirectUrl.Should().StartWith(FakeLocalPay.BaseUrl);
        system.LocalPay.PaymentOf(checkout.OrderId).AmountMinorUnits.Should().Be(5000);

        var deadline = await system.ScalarAsync<DateTime>("SELECT payment_deadline FROM orders.orders WHERE id = @p0", checkout.OrderId);
        deadline.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(1));
        (await system.ScalarAsync<long>(
            "SELECT count(*) FROM orders.scheduled_messages WHERE type = @p0 AND payload->>'orderId' = @p1",
            typeof(CheckoutDeadline).FullName!,
            checkout.OrderId.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task New_prices_apply_to_new_orders_and_placed_orders_keep_theirs()
    {
        var sale = await system.OnSaleAsync(priceMinorUnits: 2500);
        using var ana = system.PublicApi(Callers.Fan());
        var first = await ana.CheckoutAsync((await (await ana.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync()).HoldId);

        using var backOffice = system.BackOfficeApi(Callers.Organizer(sale.OrganizerId));
        var category = (await backOffice.EventDetailsAsync(sale.EventId)).PriceCategories.Single().PriceCategoryId;
        (await backOffice.PutAsJsonAsync(new Uri($"/events/{sale.EventId}/prices", UriKind.Relative), new ChangePricesRequest([new PriceChange(category, 3000)]), Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        await system.DeliverMessagesAsync();

        using var ben = system.PublicApi(Callers.Fan());
        var second = await ben.CheckoutAsync((await (await ben.HoldAsync(sale.EventId, sale.Seats[1])).PlacedAsync()).HoldId);

        (await ana.OrderAsync(first.OrderId)).TotalMinorUnits.Should().Be(2500);
        (await ben.OrderAsync(second.OrderId)).TotalMinorUnits.Should().Be(3000);
    }

    [Fact]
    public async Task At_the_deadline_the_payment_is_cancelled_and_then_the_seats_released()
    {
        var (sale, ana, checkout) = await CheckedOutAsync();

        await DeadlineAsync(checkout.OrderId);

        system.LocalPay.PaymentOf(checkout.OrderId).Status.Should().Be("expired");
        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Expired");
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(sale.Seats.Take(2));
    }

    [Fact]
    public async Task At_the_deadline_a_payment_that_already_went_through_keeps_its_seats_and_is_confirmed_by_asking_the_provider()
    {
        var (sale, ana, checkout) = await CheckedOutAsync();
        system.LocalPay.Settle(system.LocalPay.PaymentOf(checkout.OrderId), "succeeded"); // paid, webhook not here yet

        await DeadlineAsync(checkout.OrderId);

        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Pending", "the webhook should confirm it");
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().NotContain(sale.Seats.Take(2));
        var checkAt = await system.ScalarAsync<DateTime>(
            "SELECT due_at FROM orders.scheduled_messages WHERE type = @p0 AND payload->>'orderId' = @p1",
            typeof(CheckOrderPayment).FullName!,
            checkout.OrderId.ToString());
        checkAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(2), TimeSpan.FromMinutes(1));

        // No webhook within the grace period: Orders asks, Payments finds the success.
        (await system.MakeDueAsync("orders", typeof(CheckOrderPayment), checkout.OrderId)).Should().Be(1);
        await system.DeliverMessagesAsync();

        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Confirmed");
        (await system.ScalarAsync<long>(
            "SELECT count(*) FROM inventory.seats WHERE order_id = @p0 AND status = 'Sold'", checkout.OrderId)).Should().Be(2);
    }

    [Fact]
    public async Task At_the_deadline_an_unreachable_provider_is_asked_again_later_and_the_seats_stay_held()
    {
        var (sale, ana, checkout) = await CheckedOutAsync();
        system.LocalPay.MakeUnreachable(system.LocalPay.PaymentOf(checkout.OrderId).Id);

        await DeadlineAsync(checkout.OrderId);

        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Pending");
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().NotContain(sale.Seats.Take(2));
        var retryAt = await system.ScalarAsync<DateTime>(
            "SELECT due_at FROM orders.scheduled_messages WHERE type = @p0 AND payload->>'orderId' = @p1",
            typeof(CheckoutDeadline).FullName!,
            checkout.OrderId.ToString());
        retryAt.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(30), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_declined_payment_closes_the_checkout_at_once()
    {
        var (sale, ana, checkout) = await CheckedOutAsync();
        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "failed");

        (await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.failed", payment))).StatusCode.Should().Be(HttpStatusCode.OK);
        await system.DeliverMessagesAsync();

        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Failed");
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(sale.Seats.Take(2));
    }

    [Fact]
    public async Task A_deadline_after_the_order_was_paid_changes_nothing()
    {
        var (sale, ana, checkout) = await CheckedOutAsync();
        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "succeeded");
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();
        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Confirmed");

        await DeadlineAsync(checkout.OrderId);

        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Confirmed");
        payment.Status.Should().Be("succeeded", "a paid order's payment is never cancelled");
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().NotContain(sale.Seats.Take(2));
    }

    private async Task<(Sale Sale, HttpClient Fan, CheckoutStarted Checkout)> CheckedOutAsync()
    {
        var sale = await system.OnSaleAsync();
        var fan = system.PublicApi(Callers.Fan());
        var hold = await (await fan.HoldAsync(sale.EventId, sale.Seats[0], sale.Seats[1])).PlacedAsync();
        return (sale, fan, await fan.CheckoutAsync(hold.HoldId));
    }

    private async Task DeadlineAsync(Guid orderId)
    {
        (await system.MakeDueAsync("orders", typeof(CheckoutDeadline), orderId)).Should().Be(1);
        await system.DeliverMessagesAsync();
    }
}
