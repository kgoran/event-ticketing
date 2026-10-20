using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Testing;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>
/// M1b Q6: the M1b demo script (docs/demos/m1b.md) as the presenter does it, against the AppHost in
/// demo mode: a purchase becomes signed tickets, in "My tickets" and in Mailpit, and the ticket limit
/// holds. The fan is hana, so the M1a dry run's fans are left alone.
/// </summary>
public sealed class DemoM1bDryRunTests(RunningApp running)
{
    private readonly DemoScript _demo = new(running);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_purchase_becomes_signed_tickets_on_the_fan_site_and_in_the_inbox_and_the_limit_holds()
    {
        // Step 3. Hana buys two seats and pays on LocalPay's page.
        var (eventId, seats) = await _demo.SeededEventAsync("Demo Concert");
        var pair = seats.Where(s => s.Section == "B" && s.Row == "3").Take(2).Select(s => s.SeatId).ToArray();
        using var hana = await _demo.FanAsync("hana");

        var hold = await DemoScript.CreatedAsync(await DemoScript.HoldAsync(hana, eventId, pair));
        var (orderId, redirectUrl) = await DemoScript.CheckoutAsync(hana, hold.GetProperty("holdId").GetGuid());
        await DemoScript.PayAsync(redirectUrl);
        await DemoScript.OrderAsync(hana, orderId, DemoScript.AsyncStep, "Confirmed");

        // Step 4. One ticket per seat, seconds later; every code verifies with the event's published
        // public keys and nothing of ours (ADR-0051).
        var tickets = await RunningApp.EventuallyAsync(
            () => hana.GetFromJsonAsync<JsonElement>(new Uri($"/tickets?orderId={orderId}", UriKind.Relative), Ct),
            found => found.GetArrayLength() == 2,
            DemoScript.AsyncStep,
            Ct);

        using var anonymous = running.Client("publicapi");
        var keys = await anonymous.GetFromJsonAsync<JsonElement>(new Uri($"/events/{eventId}/ticket-keys", UriKind.Relative), Ct);
        foreach (var ticket in tickets.EnumerateArray())
        {
            Verifies(ticket.GetProperty("code").GetString()!, keys).Should().BeTrue();
        }

        // ...and on the fan site, as QR codes.
        using var browser = await FanSiteBrowser.SignInAsync(running, "hana");
        var page = WebUtility.HtmlDecode(await browser.PageAsync($"/tickets?order={orderId}"));
        page.Should().Contain("B · Row 3 · Seat").And.Contain("<svg");

        // Step 5. The email, in Mailpit's inbox.
        using var mailpit = running.App.CreateHttpClient("mailpit", "http");
        await RunningApp.EventuallyAsync(
            async () => (await mailpit.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/messages?limit=500", UriKind.Relative), Ct))
                .GetProperty("messages").EnumerateArray()
                .Count(message => message.GetProperty("Subject").GetString() == "Your tickets for Demo Concert"
                    && message.GetProperty("To").EnumerateArray().Any(to => to.GetProperty("Address").GetString() == "hana@example.test")),
            count => count == 1,
            DemoScript.AsyncStep,
            Ct);

        // Step 7. Two bought: seven more would make nine, past the limit of eight.
        var seven = seats.Where(s => s.Section == "C" && s.Row == "4").Take(7).Select(s => s.SeatId).ToArray();
        var refused = await DemoScript.HoldAsync(hana, eventId, seven);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("hold.ticket_limit").And.Contain("you already have 2");
    }

    /// <summary>A JWS (ES256) checked by hand: the header's key from the set, ECDSA P-256 over "header.payload".</summary>
    private static bool Verifies(string code, JsonElement keySet)
    {
        var parts = code.Split('.');
        var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement;
        header.GetProperty("alg").GetString().Should().Be("ES256");

        var key = keySet.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("kid").GetString() == header.GetProperty("kid").GetString());
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Base64Url.DecodeFromChars(key.GetProperty("x").GetString()),
                Y = Base64Url.DecodeFromChars(key.GetProperty("y").GetString()),
            },
        });

        return ecdsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256);
    }
}
