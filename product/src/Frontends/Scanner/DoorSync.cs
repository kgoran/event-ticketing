using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EventTicketing.Scanner;

/// <summary>
/// The door's link to CheckInApi (ADR-0013), whenever there is one: a token for the device's
/// client, its scans uploaded (safe to repeat), then the door list's changes since its cursor.
/// Offline nothing here runs, and the door goes on deciding from what it has.
/// </summary>
public sealed class DoorSync(HttpClient http)
{
    private (string Value, DateTimeOffset ExpiresAt)? _token;

    /// <summary>The device's event and identity, from its token's fixed claims (D61).</summary>
    public (Guid EventId, Guid DeviceId)? Identity { get; private set; }

    /// <summary>Signs the device in; false if CheckInApi refused its credentials (revoked, or mistyped).</summary>
    public async Task<bool> SignInAsync(DeviceSetup device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        using var response = await http.PostAsJsonAsync("device-token", new { device.ClientId, device.ClientSecret }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<DeviceToken>(cancellationToken))!;
        _token = (token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
        Identity = ClaimsOf(token.AccessToken);
        return Identity is not null;
    }

    /// <summary>Uploads the queued scans, then merges the door list's changes. Returns the scans the server took.</summary>
    public async Task<SyncResult> SyncAsync(DeviceSetup device, DoorList list, IReadOnlyList<PendingScan> pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(pending);
        if (_token is not { } token || token.ExpiresAt - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(30))
        {
            if (!await SignInAsync(device, cancellationToken))
            {
                return new SyncResult(false, [], "The device's credentials were refused: it may have been revoked.");
            }
        }

        var (eventId, _) = Identity!.Value;
        var uploaded = new List<Guid>();
        foreach (var batch in pending.Chunk(500))
        {
            using var request = Authorized(HttpMethod.Post, $"events/{eventId}/scans");
            request.Content = JsonContent.Create(new { scans = batch });
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new SyncResult(false, uploaded, "CheckInApi refused this device: it has been revoked.");
            }

            response.EnsureSuccessStatusCode();
            uploaded.AddRange(batch.Select(scan => scan.ScanId));
        }

        // The changes since the cursor, page by page; a first sync downloads the whole list.
        list.EventId = eventId;
        while (true)
        {
            using var request = Authorized(HttpMethod.Get, $"events/{eventId}/door-list?since={list.Cursor}");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var page = (await response.Content.ReadFromJsonAsync<DoorListPage>(cancellationToken))!;
            list.EventName = page.EventName ?? list.EventName;
            list.Keys = page.Keys?.Keys is { Count: > 0 } keys ? [.. keys] : list.Keys;
            list.Issued.UnionWith(page.Issued);
            list.Revoked.UnionWith(page.Revoked);
            foreach (var admission in page.Admitted)
            {
                list.Admitted.TryAdd(admission.TicketId, admission.At);
            }

            list.Cursor = page.Cursor;
            if (!page.More)
            {
                break;
            }
        }

        list.SyncedAt = DateTimeOffset.UtcNow;
        return new SyncResult(true, uploaded, null);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token!.Value.Value);
        return request;
    }

    private static (Guid EventId, Guid DeviceId)? ClaimsOf(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        var root = payload.RootElement;
        return root.TryGetProperty("event_id", out var eventId) && root.TryGetProperty("device_id", out var deviceId)
            && Guid.TryParse(eventId.GetString(), out var @event) && Guid.TryParse(deviceId.GetString(), out var device)
            ? (@event, device)
            : null;
    }

    private sealed record DeviceToken(string AccessToken, int ExpiresIn);

    private sealed record DoorListPage(Guid EventId, string? EventName, long Cursor, bool More, KeySet? Keys, List<Guid> Issued, List<Guid> Revoked, List<Admission> Admitted);

    private sealed record KeySet(List<TicketKey> Keys);

    private sealed record Admission(Guid TicketId, Guid DeviceId, DateTimeOffset At);
}

/// <summary>How a sync went: the scans the server took, and why it stopped if it didn't finish.</summary>
public sealed record SyncResult(bool Completed, IReadOnlyList<Guid> Uploaded, string? Problem);
