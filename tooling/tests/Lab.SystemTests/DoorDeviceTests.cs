using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static EventTicketing.Tooling.Lab.SystemTests.LabCluster;

namespace EventTicketing.Tooling.Lab.SystemTests;

/// <summary>
/// M3 CI3 (D60, D61) with the real Keycloak: the organizer registers two door devices, each signs in
/// as its own client, downloads the door list with a fan's new ticket on it, and uploads scans; the
/// second door's admission of the same ticket is a duplicate. Revoked, a device is refused at once
/// and can't sign in again.
/// </summary>
public sealed class DoorDeviceTests
{
    [Fact]
    public async Task A_registered_device_signs_in_reads_the_door_list_and_a_second_door_is_a_duplicate()
    {
        RequireLab();
        var (fanName, orderId) = await M2DemoDryRunTests.BuyOneAsync();
        using var fan = Client(PublicApi, await TokenAsync(fanName));
        var ticket = (await fan.GetFromJsonAsync<JsonElement>(new Uri($"/tickets?orderId={orderId}", UriKind.Relative), Ct))[0];
        var ticketId = ticket.GetProperty("ticketId").GetGuid();
        var eventId = ticket.GetProperty("eventId").GetGuid();

        using var organizer = Client(BackOfficeApi, await TokenAsync("olga"));
        var north = await RegisterAsync(organizer, eventId, "North gate");
        var south = await RegisterAsync(organizer, eventId, "South gate");
        using var northDevice = Client(CheckInApi, await DeviceTokenAsync(north));
        using var southDevice = Client(CheckInApi, await DeviceTokenAsync(south));

        // The ticket reaches the door list through Ticketing's TicketsIssued, a moment after the order.
        var doorList = await EventuallyAsync(
            () => northDevice.GetFromJsonAsync<JsonElement>(new Uri($"/events/{eventId}/door-list", UriKind.Relative), Ct),
            page => page.GetProperty("issued").EnumerateArray().Any(issued => issued.GetGuid() == ticketId),
            TimeSpan.FromSeconds(60),
            "the fan's ticket is on the door list");
        doorList.GetProperty("keys").GetProperty("keys").GetArrayLength().Should().BePositive("the doors verify codes offline");

        (await UploadAsync(northDevice, eventId, ticketId)).Should().Be("Admitted");
        (await UploadAsync(southDevice, eventId, ticketId)).Should().Be("Duplicate");

        // Revoked: refused at once with the token it still holds, and no new token.
        (await organizer.DeleteAsync(new Uri($"/events/{eventId}/devices/{south.GetProperty("deviceId").GetGuid()}", UriKind.Relative), Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await southDevice.GetAsync(new Uri($"/events/{eventId}/door-list", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await FluentActions.Awaiting(() => DeviceTokenAsync(south)).Should().ThrowAsync<HttpRequestException>("its Keycloak client is gone");
    }

    private static async Task<JsonElement> RegisterAsync(HttpClient organizer, Guid eventId, string name) =>
        await (await organizer.PostJsonAsync($"/events/{eventId}/devices", new { name })).CreatedAsync();

    /// <summary>The device signs in as its Keycloak client, at the token endpoint it was given.</summary>
    private static async Task<string> DeviceTokenAsync(JsonElement device)
    {
        using var keycloak = new HttpClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = device.GetProperty("clientId").GetString()!,
            ["client_secret"] = device.GetProperty("clientSecret").GetString()!,
        });
        using var response = await keycloak.PostAsync(new Uri(device.GetProperty("tokenEndpoint").GetString()!), form, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("access_token").GetString()!;
    }

    private static async Task<string> UploadAsync(HttpClient device, Guid eventId, Guid ticketId)
    {
        var scan = new { scanId = Guid.NewGuid(), ticketId, scannedAt = DateTimeOffset.UtcNow, admitted = true };
        var response = await device.PostJsonAsync($"/events/{eventId}/scans", new { scans = new[] { scan } });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct))[0].GetProperty("outcome").GetString()!;
    }
}
