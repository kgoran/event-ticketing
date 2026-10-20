namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>
/// M1b L2 (ADR-0046 update): the product against LocalPay itself when creating a payment fails once,
/// answers too late, or a stale failure follows the success. Each checkout still ends with one
/// payment and a confirmed order. Fans gus, hana and ivo are for these tests only.
/// </summary>
public sealed class LocalPayMisbehaviourTests(RunningApp running)
{
    private readonly DemoScript _demo = new(running);

    [Fact]
    public async Task A_create_that_fails_once_is_retried_with_the_same_key_and_the_order_is_confirmed()
    {
        var (fan, _) = await BuyAndPayAsync("gus", section: "D", row: "11"); // 25.06: HTTP 500 on the first create
        fan.Dispose();
    }

    [Fact]
    public async Task A_create_answered_too_late_is_retried_and_finds_the_payment_that_already_exists()
    {
        var (fan, _) = await BuyAndPayAsync("hana", section: "B", row: "11"); // 25.07: the answer comes after PublicApi's timeout
        fan.Dispose();
    }

    [Fact]
    public async Task A_stale_failure_after_the_success_leaves_the_order_confirmed()
    {
        var (fan, orderId) = await BuyAndPayAsync("ivo", section: "C", row: "11"); // 25.08: payment.failed follows

        // The stale failure arrives right after the success: give it time, then the order must not have moved.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        (await DemoScript.OrderAsync(fan, orderId, DemoScript.AsyncStep, "Confirmed", "Failed")).GetProperty("status").GetString()
            .Should().Be("Confirmed");
        fan.Dispose();
    }

    private async Task<(HttpClient Fan, Guid OrderId)> BuyAndPayAsync(string user, string section, string row)
    {
        var (eventId, seats) = await _demo.SeededEventAsync("Failure Scenarios");
        var seat = seats.First(s => s.Section == section && s.Row == row).SeatId;
        var fan = await _demo.FanAsync(user);

        var hold = await DemoScript.CreatedAsync(await DemoScript.HoldAsync(fan, eventId, seat));
        var (orderId, redirectUrl) = await DemoScript.CheckoutAsync(fan, hold.GetProperty("holdId").GetGuid());
        (await _demo.LocalPayStatusAsync(redirectUrl)).Should().Be("requires_payment", "the retry found or created exactly this payment");

        await DemoScript.PayAsync(redirectUrl);
        await DemoScript.OrderAsync(fan, orderId, DemoScript.AsyncStep, "Confirmed");
        return (fan, orderId);
    }
}
