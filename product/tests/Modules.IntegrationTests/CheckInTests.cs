using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M3 CI3 (ADR-0013, ADR-0056): an event's organizer registers a device; the device downloads the
/// door list (tickets, revocations, keys) and then only what changed; it uploads its scans, safe to
/// repeat; the first admission of a ticket counts and a second is a duplicate, raised; a revoked
/// device is refused at once.
/// </summary>
public sealed class CheckInTests(TestSystem system)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_events_organizer_or_an_admin_registers_a_device_and_nobody_else_does()
    {
        var sale = await system.OnSaleAsync();
        await system.DeliverMessagesAsync();
        var devices = new Uri($"/events/{sale.EventId}/devices", UriKind.Relative);

        using var organizer = system.BackOfficeApi(Callers.Organizer(sale.OrganizerId));
        var registered = await organizer.PostAsJsonAsync(devices, new { name = "North gate 1" }, Ct);
        registered.StatusCode.Should().Be(HttpStatusCode.Created);
        var device = (await registered.Content.ReadFromJsonAsync<RegisteredDeviceResponse>(Json, Ct))!;
        device.ClientSecret.Should().NotBeNullOrEmpty();
        device.ClientId.Should().Be("device-" + device.DeviceId.ToString("N"));

        // The client in Keycloak names the event and the device; its secret is the one shown.
        var client = system.DeviceDirectory.Client(device.ClientId)!;
        client.EventId.Should().Be(sale.EventId);
        client.ClientSecret.Should().Be(device.ClientSecret);

        using var admin = system.BackOfficeApi(Callers.Admin());
        (await admin.PostAsJsonAsync(devices, new { name = "North gate 2" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        using var another = system.BackOfficeApi(Callers.Organizer(Guid.NewGuid()));
        (await another.PostAsJsonAsync(devices, new { name = "Not mine" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var fan = system.BackOfficeApi(Callers.Fan());
        (await fan.PostAsJsonAsync(devices, new { name = "Not mine" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync(new Uri($"/events/{Guid.NewGuid()}/devices", UriKind.Relative), new { name = "Nowhere" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The list never shows a secret.
        var listed = await organizer.GetStringAsync(devices, Ct);
        listed.Should().Contain("North gate 1").And.NotContain(device.ClientSecret);
    }

    [Fact]
    public async Task A_device_downloads_the_door_list_then_only_what_changed_revocations_included()
    {
        var (sale, orderId) = await PaidOrderAsync();
        var tickets = await TicketIdsAsync(orderId);
        var deviceId = await RegisterDeviceAsync(sale);
        using var device = system.CheckInApi(Callers.Device(sale.EventId, deviceId));

        var first = await DoorListAsync(device, sale.EventId);
        first.Issued.Should().BeEquivalentTo(tickets);
        first.Revoked.Should().BeEmpty();
        first.Keys!.Keys.Should().NotBeEmpty("the device verifies codes offline with the event's public keys");
        first.Cursor.Should().BePositive();
        first.More.Should().BeFalse();

        (await DoorListAsync(device, sale.EventId, first.Cursor)).Issued.Should().BeEmpty("nothing changed since the cursor");

        using var admin = system.BackOfficeApi(Callers.Admin());
        (await admin.PostAsJsonAsync(new Uri($"/tickets/{tickets[0]}/revocation", UriKind.Relative), new { reason = "Chargeback" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        await system.DeliverMessagesAsync();

        var changes = await DoorListAsync(device, sale.EventId, first.Cursor);
        changes.Revoked.Should().Equal(tickets[0]);
        changes.Issued.Should().BeEmpty();
        changes.Cursor.Should().BeGreaterThan(first.Cursor);
    }

    [Fact]
    public async Task Only_an_active_device_of_the_event_gets_its_door_list()
    {
        var (sale, _) = await PaidOrderAsync();
        var deviceId = await RegisterDeviceAsync(sale);
        var doorList = new Uri($"/events/{sale.EventId}/door-list", UriKind.Relative);

        using var otherEvent = system.CheckInApi(Callers.Device(Guid.NewGuid(), deviceId));
        (await otherEvent.GetAsync(doorList, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var unregistered = system.CheckInApi(Callers.Device(sale.EventId, Guid.NewGuid()));
        (await unregistered.GetAsync(doorList, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var fan = system.CheckInApi(Callers.Fan());
        (await fan.GetAsync(doorList, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var device = system.CheckInApi(Callers.Device(sale.EventId, deviceId));
        (await device.GetAsync(doorList, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Revoked: refused at once, whatever token it still holds; its client is gone from Keycloak.
        using var organizer = system.BackOfficeApi(Callers.Organizer(sale.OrganizerId));
        (await organizer.DeleteAsync(new Uri($"/events/{sale.EventId}/devices/{deviceId}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await device.GetAsync(doorList, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        system.DeviceDirectory.Client("device-" + deviceId.ToString("N")).Should().BeNull();
        (await device.PostAsJsonAsync(new Uri($"/events/{sale.EventId}/scans", UriKind.Relative), Batch(Scan(Guid.NewGuid(), Guid.NewGuid())), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_first_admission_counts_a_repeated_upload_changes_nothing_and_a_second_door_is_a_duplicate()
    {
        var (sale, orderId) = await PaidOrderAsync();
        var ticket = (await TicketIdsAsync(orderId))[0];
        var north = await RegisterDeviceAsync(sale);
        var south = await RegisterDeviceAsync(sale);
        using var northDevice = system.CheckInApi(Callers.Device(sale.EventId, north));
        using var southDevice = system.CheckInApi(Callers.Device(sale.EventId, south));
        var cursor = (await DoorListAsync(southDevice, sale.EventId)).Cursor;

        var scanId = Guid.NewGuid();
        var admitted = await UploadAsync(northDevice, sale.EventId, Scan(scanId, ticket));
        admitted.Should().ContainSingle().Which.Outcome.Should().Be("Admitted");

        // The same upload again, after a lost answer: the same outcome, still one admission.
        (await UploadAsync(northDevice, sale.EventId, Scan(scanId, ticket))).Single().Outcome.Should().Be("Admitted");

        // The south door, offline until now, let the same ticket in too.
        var duplicate = (await UploadAsync(southDevice, sale.EventId, Scan(Guid.NewGuid(), ticket))).Single();
        duplicate.Outcome.Should().Be("Duplicate");
        duplicate.FirstDeviceId.Should().Be(north);

        (await system.ScalarAsync<long>(
            "SELECT count(*) FROM checkin.outbox_messages WHERE type = 'checkin.duplicate-entry-detected' AND payload->>'ticketId' = @p0", ticket.ToString()))
            .Should().Be(1);

        // A door turning a ticket away is logged, and admits nobody.
        (await UploadAsync(southDevice, sale.EventId, Scan(Guid.NewGuid(), Guid.NewGuid(), admitted: false, reason: "unknown"))).Single().Outcome.Should().Be("Refused");

        // Other doors learn of the admission at their next sync.
        var changes = await DoorListAsync(southDevice, sale.EventId, cursor);
        changes.Admitted.Should().ContainSingle().Which.Should().Match<AdmissionResponse>(a => a.TicketId == ticket && a.DeviceId == north);
    }

    [Fact]
    public async Task Two_doors_admitting_the_same_ticket_at_the_same_moment_make_one_admission()
    {
        var (sale, orderId) = await PaidOrderAsync();
        var ticket = (await TicketIdsAsync(orderId))[0];
        var devices = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RegisterDeviceAsync(sale)));

        var outcomes = await Task.WhenAll(devices.Select(async deviceId =>
        {
            using var device = system.CheckInApi(Callers.Device(sale.EventId, deviceId));
            return (await UploadAsync(device, sale.EventId, Scan(Guid.NewGuid(), ticket))).Single().Outcome;
        }));

        outcomes.Count(outcome => outcome == "Admitted").Should().Be(1);
        outcomes.Count(outcome => outcome == "Duplicate").Should().Be(5);
        (await system.ScalarAsync<long>("SELECT count(*) FROM checkin.door_list WHERE ticket_id = @p0 AND change = 3", ticket)).Should().Be(1);
    }

    private async Task<(Sale Sale, Guid OrderId)> PaidOrderAsync()
    {
        var sale = await system.OnSaleAsync();
        using var fan = system.PublicApi(Callers.Fan());
        var hold = await (await fan.HoldAsync(sale.EventId, sale.Seats[0], sale.Seats[1])).PlacedAsync();
        var checkout = await fan.CheckoutAsync(hold.HoldId);

        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "succeeded");
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();
        return (sale, checkout.OrderId);
    }

    private async Task<Guid> RegisterDeviceAsync(Sale sale)
    {
        using var organizer = system.BackOfficeApi(Callers.Organizer(sale.OrganizerId));
        var registered = await organizer.PostAsJsonAsync(new Uri($"/events/{sale.EventId}/devices", UriKind.Relative), new { name = "Gate" }, Ct);
        registered.StatusCode.Should().Be(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync(Ct));
        return (await registered.Content.ReadFromJsonAsync<RegisteredDeviceResponse>(Json, Ct))!.DeviceId;
    }

    private Task<IReadOnlyList<Guid>> TicketIdsAsync(Guid orderId) =>
        system.QueryAsync<Guid>("SELECT id FROM ticketing.tickets WHERE order_id = @p0 ORDER BY label", orderId);

    private static async Task<DoorListResponse> DoorListAsync(HttpClient device, Guid eventId, long since = 0)
    {
        var response = await device.GetAsync(new Uri($"/events/{eventId}/door-list?since={since}", UriKind.Relative), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<DoorListResponse>(Json, Ct))!;
    }

    private static async Task<IReadOnlyList<ScanResultResponse>> UploadAsync(HttpClient device, Guid eventId, params object[] scans)
    {
        var response = await device.PostAsJsonAsync(new Uri($"/events/{eventId}/scans", UriKind.Relative), Batch(scans), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<List<ScanResultResponse>>(Json, Ct))!;
    }

    private static object Batch(params object[] scans) => new { scans };

    private static object Scan(Guid scanId, Guid ticketId, bool admitted = true, string? reason = null) =>
        new { scanId, ticketId, scannedAt = DateTimeOffset.UtcNow, admitted, reason };

    private sealed record RegisteredDeviceResponse(Guid DeviceId, Guid EventId, string Name, string ClientId, string ClientSecret, Uri? TokenEndpoint);

    private sealed record DoorListResponse(Guid EventId, long Cursor, bool More, KeySetResponse? Keys, List<Guid> Issued, List<Guid> Revoked, List<AdmissionResponse> Admitted);

    private sealed record KeySetResponse(List<JsonElement> Keys);

    private sealed record AdmissionResponse(Guid TicketId, Guid DeviceId, DateTimeOffset At);

    private sealed record ScanResultResponse(Guid ScanId, string Outcome, Guid? FirstDeviceId, DateTimeOffset? FirstAdmittedAt);
}
