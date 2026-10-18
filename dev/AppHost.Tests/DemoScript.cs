using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>
/// The demo script's steps (docs/demos/m1a.md) as the presenter does them, through the real APIs
/// and LocalPay, against the AppHost in demo mode (1-minute holds, 2-minute checkouts).
/// </summary>
internal sealed class DemoScript(RunningApp running)
{
    public static readonly TimeSpan AsyncStep = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The seeded event's ID and its seats by section, row and number.</summary>
    public async Task<(Guid EventId, IReadOnlyList<(string Section, string Row, int Number, Guid SeatId)> Seats)> SeededEventAsync(string name)
    {
        (await running.SeedAsync(Ct)).Should().Be(0);

        using var anonymous = running.Client("publicapi");
        var page = await anonymous.GetFromJsonAsync<JsonElement>(new Uri($"/events?q={Uri.EscapeDataString(name)}&pageSize=50", UriKind.Relative), Ct);
        var eventId = page.GetProperty("items").EnumerateArray().First(e => e.GetProperty("name").GetString() == name).GetProperty("eventId").GetGuid();

        var details = await anonymous.GetFromJsonAsync<JsonElement>(new Uri($"/events/{eventId}", UriKind.Relative), Ct);
        var map = await anonymous.GetFromJsonAsync<JsonElement>(new Uri(details.GetProperty("seatMapUrl").GetString()!, UriKind.Relative), Ct);

        var seats = map.GetProperty("sections").EnumerateArray()
            .SelectMany(section => section.GetProperty("seats").EnumerateArray().Select(seat => (
                section.GetProperty("code").GetString()!,
                seat.GetProperty("row").GetString()!,
                seat.GetProperty("number").GetInt32(),
                seat.GetProperty("seatId").GetGuid())))
            .ToList();

        return (eventId, seats);
    }

    public async Task<HttpClient> FanAsync(string user) =>
        running.Client("publicapi", token: await running.TokenAsync(user, Ct));

    public static Task<HttpResponseMessage> HoldAsync(HttpClient fan, Guid eventId, params Guid[] seatIds) =>
        fan.PostAsJsonAsync(new Uri($"/events/{eventId}/holds", UriKind.Relative), new { seatIds }, Ct);

    public static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>POST /orders: returns the order ID and LocalPay's payment page.</summary>
    public static async Task<(Guid OrderId, string RedirectUrl)> CheckoutAsync(HttpClient fan, Guid holdId)
    {
        var checkout = await CreatedAsync(await fan.PostAsJsonAsync(new Uri("/orders", UriKind.Relative), new { holdId }, Ct));
        return (checkout.GetProperty("orderId").GetGuid(), checkout.GetProperty("redirectUrl").GetString()!);
    }

    /// <summary>The fan clicks Approve or Decline on LocalPay's page (a form POST; the browser would follow the redirect).</summary>
    public static async Task PayAsync(string redirectUrl, bool approve = true)
    {
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var response = await browser.PostAsync(new Uri(redirectUrl + (approve ? "/approve" : "/decline")), null, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    /// <summary>The return page: polls the order until its status is one of <paramref name="statuses"/>.</summary>
    public static Task<JsonElement> OrderAsync(HttpClient fan, Guid orderId, TimeSpan timeout, params string[] statuses) =>
        RunningApp.EventuallyAsync(
            () => fan.GetFromJsonAsync<JsonElement>(new Uri($"/orders/{orderId}", UriKind.Relative), Ct),
            order => statuses.Contains(order.GetProperty("status").GetString()),
            timeout,
            Ct);

    public async Task<IReadOnlyList<Guid>> AvailableSeatsAsync(Guid eventId)
    {
        using var anonymous = running.Client("publicapi");
        var availability = await anonymous.GetFromJsonAsync<JsonElement>(new Uri($"/events/{eventId}/availability", UriKind.Relative), Ct);
        return [.. availability.GetProperty("availableSeatIds").EnumerateArray().Select(id => id.GetGuid())];
    }

    /// <summary>LocalPay's own view of a payment, by the ID at the end of its checkout URL.</summary>
    public async Task<string> LocalPayStatusAsync(string redirectUrl)
    {
        using var localPay = running.Client("localpay");
        var paymentId = redirectUrl[(redirectUrl.LastIndexOf('/') + 1)..];
        var payment = await localPay.GetFromJsonAsync<JsonElement>(new Uri($"/v1/payments/{paymentId}", UriKind.Relative), Ct);
        return payment.GetProperty("status").GetString()!;
    }
}
