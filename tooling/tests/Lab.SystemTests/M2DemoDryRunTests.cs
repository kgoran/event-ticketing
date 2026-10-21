using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static EventTicketing.Tooling.Lab.SystemTests.LabCluster;

namespace EventTicketing.Tooling.Lab.SystemTests;

/// <summary>
/// M2 Q7: the M2 demo (docs/demos/m2.md) in the lab cluster, step by step, as the presenter does
/// it, against the M2 definition of done: every flow works in the cluster, PublicApi scales by hand
/// and by itself without a failed request, and a flag switches without a deploy. The deploy gate
/// and the rollout under load (steps 7 and 8) are the lab workflow's own steps. Each test leaves the
/// lab as `lab up` left it.
/// </summary>
public sealed class M2DemoDryRunTests
{
    [Fact]
    public async Task Step_3_a_purchase_in_the_cluster_becomes_tickets_and_an_email()
    {
        RequireLab();
        var (fan, _) = await BuyOneAsync();

        using var mailpit = Client(Mailpit);
        await EventuallyAsync(
            async () => (await mailpit.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/messages?limit=500", UriKind.Relative), Ct))
                .GetProperty("messages").EnumerateArray()
                .Count(message => message.GetProperty("Subject").GetString() == "Your tickets for Demo Concert"
                    && message.GetProperty("To").EnumerateArray().Any(to => to.GetProperty("Address").GetString() == $"{fan}@example.test")),
            count => count >= 1,
            TimeSpan.FromSeconds(60),
            "the tickets' email reaches Mailpit");
    }

    /// <summary>
    /// A lab fan buys one place at Demo Concert: hold, checkout, approve on LocalPay's page, the order
    /// confirmed and its ticket issued. A fan from the upper hundred, so repeated runs don't reach the
    /// ticket limit.
    /// </summary>
    internal static async Task<(string Fan, Guid OrderId)> BuyOneAsync()
    {
        using var anonymous = Client(PublicApi);
        var events = await anonymous.GetFromJsonAsync<JsonElement>(new Uri("/events?q=%22Demo%20Concert%22", UriKind.Relative), Ct);
        var eventId = events.GetProperty("items")[0].GetProperty("eventId").GetGuid();
        var availability = await anonymous.GetFromJsonAsync<JsonElement>(new Uri($"/events/{eventId}/availability", UriKind.Relative), Ct);
        var section = availability.GetProperty("sections").EnumerateArray()
            .First(s => s.GetProperty("available").GetInt32() > 0).GetProperty("section").GetString()!;

        var name = $"lab{Random.Shared.Next(101, 201)}";
        using var fan = Client(PublicApi, await TokenAsync(name));
        var hold = await (await fan.PostJsonAsync($"/events/{eventId}/holds", new { section, quantity = 1 })).CreatedAsync();
        var checkout = await (await fan.PostJsonAsync("/orders", new { holdId = hold.GetProperty("holdId").GetGuid() })).CreatedAsync();
        var orderId = checkout.GetProperty("orderId").GetGuid();

        // The fan approves on LocalPay's page; the browser would follow the redirect to the return page.
        using (var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
        {
            var approved = await browser.PostAsync(new Uri(checkout.GetProperty("redirectUrl").GetString() + "/approve"), null, Ct);
            approved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        await EventuallyAsync(
            () => fan.GetFromJsonAsync<JsonElement>(new Uri($"/orders/{orderId}", UriKind.Relative), Ct),
            order => order.GetProperty("status").GetString() == "Confirmed",
            TimeSpan.FromSeconds(60),
            "the order is confirmed after the webhook");

        await EventuallyAsync(
            () => fan.GetFromJsonAsync<JsonElement>(new Uri($"/tickets?orderId={orderId}", UriKind.Relative), Ct),
            tickets => tickets.GetArrayLength() == 1,
            TimeSpan.FromSeconds(60),
            "one ticket is issued");

        return (name, orderId);
    }

    [Fact]
    public async Task Step_4_publicapi_scales_by_hand_under_load_without_a_failed_request()
    {
        RequireLab();
        try
        {
            await RestoreAsync();
            await EventuallyAsync(PublicApiAsync, state => state.Ready == 3, TimeSpan.FromSeconds(120), "three PublicApi instances are ready");
            await LoadAsync(100);
            await Task.Delay(TimeSpan.FromSeconds(10), Ct);
            var clock = Stopwatch.StartNew();

            await ScalePublicApiAsync(5);
            await EventuallyAsync(PublicApiAsync, state => state.Ready == 5, TimeSpan.FromSeconds(120), "five instances are ready");
            await ScalePublicApiAsync(2);
            await EventuallyAsync(PublicApiAsync, state => state.Ready == 2, TimeSpan.FromSeconds(120), "two instances are left");
            await Task.Delay(TimeSpan.FromSeconds(10), Ct); // the stopped instances' 5-second grace and their last requests

            var totals = await TotalsAsync((int)Math.Ceiling(clock.Elapsed.TotalSeconds));
            totals.Completed.Should().BeGreaterThan(1_000);
            totals.Failed.Should().Be(0, "instances start and stop without failing a request (M2 definition of done)");
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task Step_5_with_auto_on_a_step_adds_instances_and_stopping_gives_them_back()
    {
        RequireLab();
        try
        {
            await RestoreAsync();
            await ScalePublicApiAsync(2);
            await EventuallyAsync(PublicApiAsync, state => state.Wanted == 2 && state.Ready == 2, TimeSpan.FromSeconds(120), "two instances to start from");
            var clock = Stopwatch.StartNew();

            // 150 requests/s, well past what two instances carry at the CPU target on any machine: how
            // much CPU a request costs differs between the laptop and CI's runners (the demo uses 84).
            await AutoAsync(on: true);
            await LoadAsync(150);
            await EventuallyAsync(PublicApiAsync, state => state.Ready >= 4, TimeSpan.FromSeconds(240), "the autoscaler adds instances for 150 requests/s", AutoscalerAsync);
            await LoadAsync(0);
            await EventuallyAsync(PublicApiAsync, state => state.Wanted <= 2, TimeSpan.FromSeconds(180), "the autoscaler gives them back, demo speed: 30 s", AutoscalerAsync);

            var totals = await TotalsAsync(Math.Min(300, (int)Math.Ceiling(clock.Elapsed.TotalSeconds)));
            totals.Failed.Should().Be(0, "scaling out and in fails no request");
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task Step_6_a_flag_pauses_holds_without_a_deploy_and_browsing_goes_on()
    {
        RequireLab();
        try
        {
            await RestoreAsync();
            await LoadAsync(20, holdShare: 0.5);
            await Task.Delay(TimeSpan.FromSeconds(10), Ct);
            var clock = Stopwatch.StartNew();

            // About 10 holds a second: within a minute or so every instance refuses them all.
            await FlagAsync("PauseHolds", on: true);
            await EventuallyAsync(() => TotalsAsync(5), totals => totals.Refused >= 45 && totals.Ok >= 40, TimeSpan.FromSeconds(150), "every instance pauses holds, and browsing goes on");

            await FlagAsync("PauseHolds", on: false);
            await EventuallyAsync(() => TotalsAsync(5), totals => totals.Refused <= 1 && totals.Ok >= 120, TimeSpan.FromSeconds(150), "every instance takes holds again");

            var totals = await TotalsAsync(Math.Min(300, (int)Math.Ceiling(clock.Elapsed.TotalSeconds)));
            totals.Failed.Should().Be(0, "a paused hold is refused (503 with Retry-After), not failed");
        }
        finally
        {
            await RestoreAsync();
        }
    }
}
