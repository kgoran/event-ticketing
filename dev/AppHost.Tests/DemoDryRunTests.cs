using System.Net;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>
/// M1a Q4: a dry run of the demo (docs/demos/m1a.md). Steps 3–5 and 8 here; step 7, which waits for
/// expiry, runs in the classes below at the same time (xUnit runs test classes in parallel).
/// </summary>
public sealed class DemoDryRunTests(RunningApp running)
{
    private readonly DemoScript _demo = new(running);

    [Fact]
    public async Task Steps_3_to_5_browse_hold_one_fan_wins_pay_and_the_order_is_confirmed()
    {
        var (eventId, seats) = await _demo.SeededEventAsync("Demo Concert");
        var pair = seats.Where(s => s.Section == "C" && s.Row == "6").Take(2).Select(s => s.SeatId).ToArray();

        // Step 4: Fan A holds two seats and gets 201; Fan B tries the same seats and gets 409.
        using var ana = await _demo.FanAsync("ana");
        using var ben = await _demo.FanAsync("ben");
        var hold = await DemoScript.CreatedAsync(await DemoScript.HoldAsync(ana, eventId, pair));
        (await DemoScript.HoldAsync(ben, eventId, pair)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Step 5: checkout, approve on LocalPay's page, the order goes Pending → Confirmed.
        var (orderId, redirectUrl) = await DemoScript.CheckoutAsync(ana, hold.GetProperty("holdId").GetGuid());
        (await DemoScript.OrderAsync(ana, orderId, DemoScript.AsyncStep, "Pending")).GetProperty("totalMinorUnits").GetInt64().Should().Be(2 * 5_000);
        await DemoScript.PayAsync(redirectUrl);

        await DemoScript.OrderAsync(ana, orderId, DemoScript.AsyncStep, "Confirmed");
        (await _demo.AvailableSeatsAsync(eventId)).Should().NotContain(pair);
    }

    [Fact]
    public async Task Step_8_a_duplicate_webhook_confirms_the_order_once()
    {
        var (eventId, seats) = await _demo.SeededEventAsync("Failure Scenarios");
        var seat = seats.First(s => s.Section == "A" && s.Row == "1").SeatId; // 25.03: LocalPay sends the webhook twice

        using var cid = await _demo.FanAsync("cid");
        var hold = await DemoScript.CreatedAsync(await DemoScript.HoldAsync(cid, eventId, seat));
        var (orderId, redirectUrl) = await DemoScript.CheckoutAsync(cid, hold.GetProperty("holdId").GetGuid());
        await DemoScript.PayAsync(redirectUrl);

        await DemoScript.OrderAsync(cid, orderId, DemoScript.AsyncStep, "Confirmed");

        // Both deliveries have been handled by now; the order stays Confirmed, paid once.
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var order = await DemoScript.OrderAsync(cid, orderId, DemoScript.AsyncStep, "Confirmed");
        order.GetProperty("totalMinorUnits").GetInt64().Should().Be(2_503);
    }

    [Fact]
    public async Task Step_8_a_declined_payment_gives_the_seat_back_at_once()
    {
        var (eventId, seats) = await _demo.SeededEventAsync("Failure Scenarios");
        var seat = seats.First(s => s.Section == "C").SeatId; // 25.01: approving declines

        using var dan = await _demo.FanAsync("dan");
        var hold = await DemoScript.CreatedAsync(await DemoScript.HoldAsync(dan, eventId, seat));
        var (orderId, redirectUrl) = await DemoScript.CheckoutAsync(dan, hold.GetProperty("holdId").GetGuid());
        await DemoScript.PayAsync(redirectUrl);

        await DemoScript.OrderAsync(dan, orderId, DemoScript.AsyncStep, "Failed");
        (await _demo.AvailableSeatsAsync(eventId)).Should().Contain(seat);
    }
}

/// <summary>Step 7, first half: an abandoned hold is claimable the moment it expires (demo mode: 1 minute).</summary>
public sealed class DemoHoldExpiryTests(RunningApp running)
{
    private readonly DemoScript _demo = new(running);

    [Fact]
    public async Task Step_7_an_abandoned_hold_is_claimable_the_moment_it_expires()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (eventId, seats) = await _demo.SeededEventAsync("Demo Concert");
        var seat = seats.First(s => s.Section == "D" && s.Row == "10").SeatId;

        using var eva = await _demo.FanAsync("eva");
        using var fil = await _demo.FanAsync("fil");
        var hold = await DemoScript.CreatedAsync(await DemoScript.HoldAsync(eva, eventId, seat));
        var expiresAt = hold.GetProperty("expiresAt").GetDateTimeOffset();
        expiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(1), TimeSpan.FromSeconds(15), "demo mode holds last a minute");

        // Fil keeps trying; he gets the seat as soon as Eva's hold runs out, and not before.
        var deadline = expiresAt + TimeSpan.FromSeconds(30);
        while (true)
        {
            var attempt = await DemoScript.HoldAsync(fil, eventId, seat);
            if (attempt.StatusCode == HttpStatusCode.Created)
            {
                var claimedAt = DateTimeOffset.UtcNow;
                claimedAt.Should().BeOnOrAfter(expiresAt.AddSeconds(-1), "a held seat can't be taken early");
                (claimedAt - expiresAt).Should().BeLessThan(TimeSpan.FromSeconds(5), "an expired seat is claimable at once (ADR-0011)");
                break;
            }

            attempt.StatusCode.Should().Be(HttpStatusCode.Conflict);
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the seat should have been free by now");
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }
}

/// <summary>
/// Step 7, second half, and the last failure of step 8: at the 2-minute checkout deadline an
/// abandoned checkout has its LocalPay payment expired first and its seats released second; a
/// payment that completes at that very moment still ends consistent (Confirmed, seat sold).
/// </summary>
public sealed class DemoCheckoutDeadlineTests(RunningApp running)
{
    private static readonly TimeSpan PastTheDeadline = TimeSpan.FromMinutes(3);

    private readonly DemoScript _demo = new(running);

    [Fact]
    public async Task Step_7_and_8_at_the_deadline_abandoned_checkouts_close_and_a_racing_payment_wins()
    {
        var concert = await _demo.SeededEventAsync("Demo Concert");
        var scenarios = await _demo.SeededEventAsync("Failure Scenarios");
        var abandonedSeat = concert.Seats.First(s => s.Section == "B" && s.Row == "15").SeatId;
        var racingSeat = scenarios.Seats.First(s => s.Section == "B").SeatId; // 25.05: completes during expiry

        using var ben = await _demo.FanAsync("ben");
        using var eva = await _demo.FanAsync("eva");
        var abandoned = await DemoScript.CheckoutAsync(ben, (await DemoScript.CreatedAsync(await DemoScript.HoldAsync(ben, concert.EventId, abandonedSeat))).GetProperty("holdId").GetGuid());
        var racing = await DemoScript.CheckoutAsync(eva, (await DemoScript.CreatedAsync(await DemoScript.HoldAsync(eva, scenarios.EventId, racingSeat))).GetProperty("holdId").GetGuid());

        // Nobody pays. At the deadline Orders cancels the payments at LocalPay, then releases the seats.
        await DemoScript.OrderAsync(ben, abandoned.OrderId, PastTheDeadline, "Expired");
        (await _demo.LocalPayStatusAsync(abandoned.RedirectUrl)).Should().Be("expired");
        (await _demo.AvailableSeatsAsync(concert.EventId)).Should().Contain(abandonedSeat);

        // LocalPay completes the racing payment as it's being expired: the webhook confirms it.
        await DemoScript.OrderAsync(eva, racing.OrderId, PastTheDeadline, "Confirmed");
        (await _demo.LocalPayStatusAsync(racing.RedirectUrl)).Should().Be("succeeded");
        (await _demo.AvailableSeatsAsync(scenarios.EventId)).Should().NotContain(racingSeat);
    }
}
