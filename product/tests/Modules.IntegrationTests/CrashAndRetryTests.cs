using System.Net;
using System.Net.Http.Json;
using EventTicketing.Inventory.Application.Holds;
using EventTicketing.Inventory.Contracts;
using EventTicketing.Inventory.Infrastructure.Endpoints;
using EventTicketing.Orders.Application.Checkout;
using EventTicketing.Orders.Infrastructure;
using EventTicketing.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1a Q2: the rows of ADR-0009's crash table that apply in M1a, and requests that arrive twice.
/// Each crash is reproduced by stopping the flow where the crash would, then letting recovery run.
/// (Duplicate webhooks: PaymentsTests and PaidOrderTests.)
/// </summary>
public sealed class CrashAndRetryTests(TestSystem system)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Crash_after_BeginCheckout_the_backstop_gives_the_seats_back()
    {
        var sale = await system.OnSaleAsync();
        var fan = "fan-" + Guid.NewGuid().ToString("N");
        using var client = system.PublicApi(TestTokens.Create(fan, ["fan"]));
        var hold = await (await client.HoldAsync(sale.EventId, sale.Seats[0], sale.Seats[1])).PlacedAsync();

        // Inventory moved the hold into checkout; Orders crashed before creating the order.
        var begun = await system.InWorkersAsync(services =>
            services.GetRequiredService<IInventoryApi>().BeginCheckoutAsync(hold.HoldId, Guid.NewGuid(), fan, Ct));
        begun.Outcome.Should().Be(BeginCheckoutOutcome.Started);
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().NotContain(sale.Seats.Take(2));

        // Nobody acts; the backstop passes and the sweeper tidies up.
        await system.ExpireHoldAsync(hold.HoldId);
        await system.SweepNowAsync();

        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(sale.Seats.Take(2));
        using var ben = system.PublicApi(Callers.Fan());
        await (await ben.HoldAsync(sale.EventId, sale.Seats[0], sale.Seats[1])).PlacedAsync();
    }

    [Fact]
    public async Task Crash_after_the_order_was_created_the_deadline_releases_the_seats()
    {
        const long price = 7_701; // a price no other test uses, so LocalPay fails only for this checkout
        var sale = await system.OnSaleAsync(priceMinorUnits: price);
        using var ana = system.PublicApi(Callers.Fan());
        var hold = await (await ana.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync();

        // The order exists, but the payment session never got created.
        system.LocalPay.GoDownForAmount(price);
        var response = await ana.PostAsJsonAsync(new Uri("/orders", UriKind.Relative), new StartCheckoutRequest(hold.HoldId), Ct);
        response.StatusCode.Should().NotBe(HttpStatusCode.Created);
        system.LocalPay.ComeBackForAmount(price);
        var orderId = await system.ScalarAsync<Guid>("SELECT id FROM orders.orders WHERE hold_id = @p0", hold.HoldId);

        (await system.MakeDueAsync("orders", typeof(CheckoutDeadline), orderId)).Should().Be(1);
        await system.DeliverMessagesAsync();

        (await ana.OrderAsync(orderId)).Status.Should().Be("Expired");
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(sale.Seats[0]);
    }

    [Fact]
    public async Task A_lost_provider_response_is_recovered_by_asking_again_and_creates_one_payment()
    {
        const long price = 7_702;
        var sale = await system.OnSaleAsync(priceMinorUnits: price);
        using var ana = system.PublicApi(Callers.Fan());
        var hold = await (await ana.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync();

        // LocalPay created the payment, but its answer never reached us.
        system.LocalPay.LoseNextResponseForAmount(price);
        var first = await ana.PostAsJsonAsync(new Uri("/orders", UriKind.Relative), new StartCheckoutRequest(hold.HoldId), Ct);
        first.StatusCode.Should().NotBe(HttpStatusCode.Created);

        // The fan tries again: the order is resumed, and the order ID as idempotency key finds the same payment.
        var retry = await ana.CheckoutAsync(hold.HoldId);

        system.LocalPay.PaymentsFor(retry.OrderId).Should().ContainSingle()
            .Which.Id.Should().Be(system.LocalPay.PaymentOf(retry.OrderId).Id);
        retry.RedirectUrl.Should().EndWith(system.LocalPay.PaymentOf(retry.OrderId).Id);
        (await system.ScalarAsync<long>("SELECT count(*) FROM orders.orders WHERE hold_id = @p0", hold.HoldId)).Should().Be(1);
    }

    [Fact]
    public async Task Crash_after_ConfirmHold_before_the_order_was_confirmed_the_redelivery_finishes_the_job()
    {
        var sale = await system.OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());
        var hold = await (await ana.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync();
        var checkout = await ana.CheckoutAsync(hold.HoldId);
        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "succeeded");

        // Inventory had already sold the seats when Orders' transaction died.
        var confirmed = await system.InWorkersAsync(services =>
            services.GetRequiredService<IInventoryApi>().ConfirmHoldAsync(hold.HoldId, checkout.OrderId, Ct));
        confirmed.Outcome.Should().Be(ConfirmHoldOutcome.Confirmed);

        // PaymentSucceeded is (re)delivered: confirming again is allowed for the same order.
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();

        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Confirmed");
        (await system.ScalarAsync<long>("SELECT count(*) FROM orders.outbox_messages WHERE type = 'orders.order-confirmed' AND payload->>'orderId' = @p0", checkout.OrderId.ToString()))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_repeated_hold_request_with_the_same_idempotency_key_returns_the_same_hold()
    {
        var sale = await system.OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());
        var key = Guid.NewGuid().ToString();

        var first = await (await PostWithKeyAsync(ana, $"/events/{sale.EventId}/holds", new PlaceHoldRequest(SeatIds: [sale.Seats[0]]), key)).PlacedAsync();
        var again = await (await PostWithKeyAsync(ana, $"/events/{sale.EventId}/holds", new PlaceHoldRequest(SeatIds: [sale.Seats[0]]), key)).PlacedAsync();

        again.HoldId.Should().Be(first.HoldId);
        (await system.ScalarAsync<long>("SELECT count(*) FROM inventory.holds WHERE event_id = @p0", sale.EventId)).Should().Be(1);

        // Without a key, a second hold for the same event is refused: one per fan.
        (await ana.HoldAsync(sale.EventId, sale.Seats[1])).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_repeated_checkout_returns_the_same_order_and_payment_page_with_or_without_a_key()
    {
        var sale = await system.OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());
        var hold = await (await ana.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync();
        var key = Guid.NewGuid().ToString();

        var first = await ReadCheckoutAsync(await PostWithKeyAsync(ana, "/orders", new StartCheckoutRequest(hold.HoldId), key));
        var sameKey = await ReadCheckoutAsync(await PostWithKeyAsync(ana, "/orders", new StartCheckoutRequest(hold.HoldId), key));
        var noKey = await ana.CheckoutAsync(hold.HoldId);

        sameKey.Should().Be(first);
        noKey.Should().Be(first);
        system.LocalPay.PaymentsFor(first.OrderId).Should().ContainSingle();

        // Someone else can't take the order over through the hold.
        using var ben = system.PublicApi(Callers.Fan());
        (await ben.PostAsJsonAsync(new Uri("/orders", UriKind.Relative), new StartCheckoutRequest(hold.HoldId), Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static async Task<HttpResponseMessage> PostWithKeyAsync<T>(HttpClient client, string path, T body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<CheckoutStarted> ReadCheckoutAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CheckoutStarted>(Ct))!;
    }
}
