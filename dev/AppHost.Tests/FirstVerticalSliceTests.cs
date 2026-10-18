using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>
/// M1a S2: one event goes hold → checkout → LocalPay "Approve" → webhook → order confirmed, end to
/// end through the real APIs, the outbox, Workers and the provider simulator.
/// </summary>
public sealed class FirstVerticalSliceTests(RunningApp running)
{
    private static readonly TimeSpan AsyncStepTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_fan_holds_seats_pays_with_LocalPay_and_the_order_is_confirmed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // An organizer creates and publishes an event with 10 seats at 25.00 EUR.
        using var olga = running.Client("backofficeapi", token: await running.TokenAsync("olga", cancellationToken));
        var created = await olga.PostAsJsonAsync(
            new Uri("/events", UriKind.Relative),
            new
            {
                organizerId = SeededOrganizer.Id,
                name = "First slice concert",
                startsAt = DateTimeOffset.UtcNow.AddMonths(1),
                currency = "EUR",
                priceCategories = new[] { new { name = "Standard", priceMinorUnits = 2500 } },
                sections = new[] { new { name = "A", rows = 2, seatsPerRow = 5, priceCategory = "Standard" } },
            },
            cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var eventId = (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("eventId").GetGuid();

        (await olga.PostAsync(new Uri($"/events/{eventId}/publish", UriKind.Relative), null, cancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // EventPublished travels through the outbox to Inventory, which creates the seats.
        using var anonymous = running.Client("publicapi");
        var seats = await RunningApp.EventuallyAsync(
            async () => await anonymous.GetFromJsonAsync<JsonElement[]>(new Uri($"/events/{eventId}/seats", UriKind.Relative), cancellationToken) ?? [],
            current => current.Length == 10,
            AsyncStepTimeout,
            cancellationToken);
        Guid[] seatIds = [.. seats.Take(2).Select(seat => seat.GetProperty("seatId").GetGuid())];

        // Ana holds two seats; Ben can't get the same ones.
        using var ana = running.Client("publicapi", token: await running.TokenAsync("ana", cancellationToken));
        using var ben = running.Client("publicapi", token: await running.TokenAsync("ben", cancellationToken));

        var hold = await ana.PostAsJsonAsync(new Uri($"/events/{eventId}/holds", UriKind.Relative), new { seatIds }, cancellationToken);
        hold.StatusCode.Should().Be(HttpStatusCode.Created);
        var holdId = (await hold.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("holdId").GetGuid();

        (await ben.PostAsJsonAsync(new Uri($"/events/{eventId}/holds", UriKind.Relative), new { seatIds }, cancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Ana checks out and gets LocalPay's payment page.
        var checkout = await ana.PostAsJsonAsync(new Uri("/orders", UriKind.Relative), new { holdId }, cancellationToken);
        checkout.StatusCode.Should().Be(HttpStatusCode.Created);
        var checkoutBody = await checkout.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var orderId = checkoutBody.GetProperty("orderId").GetGuid();
        var redirectUrl = checkoutBody.GetProperty("redirectUrl").GetString()!;

        // She clicks Approve (a form POST in a browser); LocalPay sends the signed webhook and redirects back.
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var approved = await browser.PostAsync(new Uri(redirectUrl + "/approve"), null, cancellationToken);
        approved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Webhook → WebhookIngress → Workers → PaymentSucceeded → Orders → Inventory → OrderConfirmed.
        var order = await RunningApp.EventuallyAsync(
            () => ana.GetFromJsonAsync<JsonElement>(new Uri($"/orders/{orderId}", UriKind.Relative), cancellationToken),
            current => current.GetProperty("status").GetString() == "Confirmed",
            AsyncStepTimeout,
            cancellationToken);

        order.GetProperty("totalMinorUnits").GetInt64().Should().Be(5000);
        order.GetProperty("currency").GetString().Should().Be("EUR");

        var after = await anonymous.GetFromJsonAsync<JsonElement[]>(new Uri($"/events/{eventId}/seats", UriKind.Relative), cancellationToken);
        after!.Where(seat => seatIds.Contains(seat.GetProperty("seatId").GetGuid()))
            .Should().OnlyContain(seat => !seat.GetProperty("available").GetBoolean(), "the two seats are sold");
    }
}
