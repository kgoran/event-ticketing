using System.Net;
using System.Net.Http.Json;
using EventTicketing.Inventory.Application.Holds;
using EventTicketing.Inventory.Infrastructure.Endpoints;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1a Q1, target T2 (ADR-0015): 500 overlapping hold attempts on 100 seats at once, then every
/// hold checked out and paid, some only after their seats were lost. The invariants are checked
/// through the APIs, the provider and Inventory's rows.
/// </summary>
public sealed class ConcurrencyTests(TestSystem system)
{
    private const int Attempts = 500;
    private const int Capacity = 100;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Five_hundred_overlapping_holds_on_a_hundred_seats_never_sell_a_seat_twice()
    {
        var sale = await system.OnSaleAsync(rows: 10, seatsPerRow: 10);
        sale.Seats.Should().HaveCount(Capacity);
        var run = Guid.NewGuid().ToString("N")[..8];
        var random = new Random(20261019);

        // 1. 500 fans at once. Half pick 2–4 adjacent seats, so requests overlap heavily; half ask
        //    for the best available seat.
        var requests = Enumerable.Range(0, Attempts)
            .Select(i => (Fan: $"q1-{run}-{i}", Body: i % 2 == 0 ? Adjacent(sale, random) : new PlaceHoldRequest(Section: "A", Quantity: 1)))
            .ToList();

        var responses = await Task.WhenAll(requests.Select(async request =>
        {
            using var fan = system.PublicApi(Callers.Fan(request.Fan));
            var response = await fan.PostAsJsonAsync(new Uri($"/events/{sale.EventId}/holds", UriKind.Relative), request.Body, Ct);
            return (request.Fan, response.StatusCode, Hold: response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<HoldPlaced>(Ct) : null);
        }));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        var holds = responses.Where(r => r.Hold is not null).Select(r => (r.Fan, Hold: r.Hold!)).ToList();

        // 2. No seat is in two holds, and Inventory's rows say exactly what the holds say.
        holds.SelectMany(h => h.Hold.SeatIds).Should().OnlyHaveUniqueItems();
        (await HeldSeatsAsync(sale)).Should().BeEquivalentTo(holds.SelectMany(h => h.Hold.SeatIds));

        // 3. Whatever is left can still be held, one seat at a time, until all 100 are.
        for (var topUp = 0; ; topUp++)
        {
            var fan = $"q1-{run}-topup-{topUp}";
            using var client = system.PublicApi(Callers.Fan(fan));
            var response = await client.HoldBestAsync(sale.EventId, "A", 1);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                break;
            }

            holds.Add((fan, await response.PlacedAsync()));
        }

        (await HeldSeatsAsync(sale)).Should().BeEquivalentTo(sale.Seats, "every seat is held exactly once");

        // 4. Every hold checks out and pays. For every tenth, the hold runs past its backstop first
        //    and someone else takes one of its seats, so its payment arrives for lost seats.
        var losers = holds.Where((_, index) => index % 10 == 0).Select(h => h.Hold.HoldId).ToHashSet();
        using var gate = new SemaphoreSlim(16);
        var orders = await Task.WhenAll(holds.Select(async h =>
        {
            await gate.WaitAsync(Ct);
            try
            {
                using var fan = system.PublicApi(Callers.Fan(h.Fan));
                var checkout = await fan.CheckoutAsync(h.Hold.HoldId);
                var payment = system.LocalPay.PaymentOf(checkout.OrderId);

                if (losers.Contains(h.Hold.HoldId))
                {
                    await system.ExpireHoldAsync(h.Hold.HoldId);
                    using var thief = system.PublicApi(Callers.Fan($"{h.Fan}-thief"));
                    await (await thief.HoldAsync(sale.EventId, h.Hold.SeatIds[0])).PlacedAsync();
                }

                system.LocalPay.Settle(payment, "succeeded");
                (await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment))).StatusCode.Should().Be(HttpStatusCode.OK);
                return (h.Fan, h.Hold, checkout.OrderId, Payment: payment);
            }
            finally
            {
                gate.Release();
            }
        }));

        await system.DeliverMessagesAsync();

        // 5. T2: every paid order ends Confirmed or Refunded; a Confirmed order owns exactly its
        //    seats; a refunded order got exactly one refund and no seats; no seat is sold twice;
        //    seat states add up to capacity.
        var sold = new List<Guid>();
        foreach (var (fan, hold, orderId, payment) in orders)
        {
            using var client = system.PublicApi(Callers.Fan(fan));
            var order = await client.OrderAsync(orderId);
            var owned = await system.QueryAsync<Guid>("SELECT seat_id FROM inventory.seats WHERE order_id = @p0 AND status = 'Sold'", orderId);

            if (losers.Contains(hold.HoldId))
            {
                order.Status.Should().Be("Refunded", $"order {orderId} lost a seat");
                owned.Should().BeEmpty();
                system.LocalPay.RefundsOf(payment.Id).Should().ContainSingle().Which.AmountMinorUnits.Should().Be(order.TotalMinorUnits);
            }
            else
            {
                order.Status.Should().Be("Confirmed", $"order {orderId} kept its seats");
                owned.Should().BeEquivalentTo(order.Seats.Select(s => s.SeatId)).And.BeEquivalentTo(hold.SeatIds);
                system.LocalPay.RefundsOf(payment.Id).Should().BeEmpty();
                sold.AddRange(owned);
            }
        }

        sold.Should().OnlyHaveUniqueItems();
        var states = await system.QueryAsync<long>("SELECT count(*) FROM inventory.seats WHERE event_id = @p0 GROUP BY status", sale.EventId);
        states.Sum().Should().Be(Capacity);
        (await system.ScalarAsync<long>("SELECT count(*) FROM inventory.seats WHERE event_id = @p0 AND status = 'Sold'", sale.EventId))
            .Should().Be(sold.Count);
    }

    /// <summary>2–4 adjacent seats in a random row, like a group of friends.</summary>
    private static PlaceHoldRequest Adjacent(Sale sale, Random random)
    {
        var row = random.Next(1, 11).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var count = random.Next(2, 5);
        var first = random.Next(1, 12 - count);
        return new PlaceHoldRequest(SeatIds: [.. sale.SeatIdsAt([.. Enumerable.Range(first, count).Select(number => (row, number))])]);
    }

    private Task<IReadOnlyList<Guid>> HeldSeatsAsync(Sale sale) =>
        system.QueryAsync<Guid>("SELECT seat_id FROM inventory.seats WHERE event_id = @p0 AND status = 'Held' AND held_until > now()", sale.EventId);
}
