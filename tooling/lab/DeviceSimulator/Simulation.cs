using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EventTicketing.Tooling.DeviceSimulator;

public sealed class SimulationException(string message) : Exception(message);

/// <summary>
/// One T8 run (ADR-0015): register the devices as an organizer would, let each sign in and download
/// the door list, then sync every interval, uploading its share of the scans and fetching the changes.
/// Half-way, an admin revokes a ticket, and each device's first sync that has it is timed. At the
/// end the devices are revoked again, so Keycloak isn't left with their clients.
/// </summary>
public sealed class Simulation(SimulationOptions options, TimeProvider time)
{
    /// <summary>T8: 99% of the devices' requests answered within this.</summary>
    public static readonly TimeSpan AnswerTarget = TimeSpan.FromMilliseconds(500);

    /// <summary>T8: a revocation reaches every online device within this.</summary>
    public static readonly TimeSpan RevocationTarget = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Connections kept for the whole run, as each real device keeps its own. Recycled every minute,
    // all 200 reconnected at once, and that storm through Docker's port forwarding stalled every
    // request for a second and a half; Traefik balances request by request anyway.
    private readonly HttpClient _http = new(new SocketsHttpHandler { MaxConnectionsPerServer = 512, PooledConnectionLifetime = TimeSpan.FromMinutes(30) })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private readonly ConcurrentBag<Sample> _samples = [];
    private readonly ConcurrentDictionary<Guid, TimeSpan> _revocationSeen = [];
    private volatile RevocationProbe? _probe;
    private (int Bytes, int Entries) _firstDownload;

    public async Task<Report> RunAsync(CancellationToken cancellationToken)
    {
        var organizer = await UserTokenAsync("olga", cancellationToken);
        var admin = await UserTokenAsync("adam", cancellationToken);
        var eventId = await EventIdAsync(cancellationToken);
        Console.WriteLine($"T8: {options.Devices} devices for {options.EventName}, a sync every {options.SyncInterval.TotalSeconds:0} s, {options.ScansPerSecond:0.#} scans/s in all, {options.Duration.TotalSeconds:0} s.");

        var devices = await RegisterAsync(eventId, organizer, cancellationToken);
        try
        {
            using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            run.CancelAfter(options.Duration);
            var started = time.GetTimestamp();
            var startedAt = time.GetUtcNow();
            var loops = devices.Select((device, index) => DeviceLoopAsync(device, index, run.Token)).ToList();
            var probe = options.Revoke ? ProbeAsync(eventId, admin, devices, run.Token) : Task.CompletedTask;
            await Task.WhenAll(loops.Append(probe));
            return new Report(options.Devices, [.. _samples], _probe, [.. _revocationSeen.Values], time.GetElapsedTime(started), startedAt, startedAt + 2 * options.SyncInterval, _firstDownload);
        }
        finally
        {
            await RevokeAsync(eventId, organizer, devices);
        }
    }

    private async Task DeviceLoopAsync(Device device, int index, CancellationToken cancellationToken)
    {
        var random = new Random(index);
        try
        {
            // Devices start spread over one interval, as they would at a venue.
            await Task.Delay(options.SyncInterval * random.NextDouble(), time, cancellationToken);
            await SignInAsync(device, cancellationToken);
            await SyncAsync(device, [], Kind.Download, cancellationToken);
            using var timer = new PeriodicTimer(options.SyncInterval, time);
            var perSync = options.ScansPerSecond * options.SyncInterval.TotalSeconds / options.Devices;
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Its share of the scans: tickets not yet on its list, which the door lets in.
                var count = (int)perSync + (random.NextDouble() < perSync % 1 ? 1 : 0);
                var scans = Enumerable.Range(0, count)
                    .Select(_ => new { scanId = Guid.NewGuid(), ticketId = Guid.NewGuid(), scannedAt = time.GetUtcNow(), admitted = true })
                    .ToArray();
                await SyncAsync(device, scans, Kind.Sync, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run is over.
        }
    }

    /// <summary>Uploads the scans if any, then the door list's changes since the device's cursor.</summary>
    private async Task SyncAsync(Device device, object[] scans, Kind kind, CancellationToken cancellationToken)
    {
        if (device.TokenExpiresAt - time.GetUtcNow() < TimeSpan.FromSeconds(30))
        {
            await SignInAsync(device, cancellationToken);
        }

        if (scans.Length > 0)
        {
            await MeasureAsync(Kind.Upload, () => _http.SendAsync(Authorized(HttpMethod.Post, $"events/{device.EventId}/scans", device, new { scans }), cancellationToken), cancellationToken);
        }

        var more = true;
        while (more)
        {
            using var response = await MeasureAsync(kind, () => _http.SendAsync(Authorized(HttpMethod.Get, $"events/{device.EventId}/door-list?since={device.Cursor}", device), cancellationToken), cancellationToken);
            if (response is not { IsSuccessStatusCode: true })
            {
                return;
            }

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var page = JsonSerializer.Deserialize<DoorListPage>(body, Json)!;
            if (kind == Kind.Download && device.Cursor == 0)
            {
                _firstDownload = (body.Length, page.Issued.Count + page.Revoked.Count + page.Admitted.Count);
            }
            device.Cursor = page.Cursor;
            device.Issued.UnionWith(page.Issued);
            device.Revoked.UnionWith(page.Revoked);
            more = page.More;
            if (_probe is { } probe && page.Revoked.Contains(probe.TicketId))
            {
                _revocationSeen.TryAdd(device.DeviceId, time.GetElapsedTime(probe.RevokedAt));
            }
        }
    }

    /// <summary>Half-way: an admin revokes one of the event's tickets; the devices' loops time its arrival.</summary>
    private async Task ProbeAsync(Guid eventId, string admin, IReadOnlyList<Device> devices, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(options.Duration / 2, time, cancellationToken);
            var ticket = devices.SelectMany(device => device.Issued.Except(device.Revoked)).FirstOrDefault();
            if (ticket == Guid.Empty)
            {
                Console.WriteLine("No ticket on the door list to revoke: buy one first (the M2 demo's step 3), or run without the probe (--no-revoke).");
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BackOfficeApi, $"tickets/{ticket}/revocation"))
            {
                Content = JsonContent.Create(new { reason = "T8 revocation probe" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                Console.WriteLine($"Ticket {ticket} was already revoked by an earlier run: the probe measures nothing this time.");
                return;
            }

            response.EnsureSuccessStatusCode();
            _probe = new RevocationProbe(ticket, time.GetTimestamp());
            Console.WriteLine($"Revoked ticket {ticket}; timing until every device has it.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run ended first.
        }
    }

    private async Task<HttpResponseMessage?> MeasureAsync(Kind kind, Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        try
        {
            var response = await send();
            _samples.Add(new Sample(kind, time.GetElapsedTime(started), response.IsSuccessStatusCode, response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}", time.GetUtcNow()));
            if (kind == Kind.Upload)
            {
                response.Dispose();
                return null;
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run ended while it was on its way: not an answer that never came.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _samples.Add(new Sample(kind, time.GetElapsedTime(started), false, exception is TaskCanceledException ? "no answer in 10 s" : exception.GetType().Name, time.GetUtcNow()));
            return null;
        }
    }

    private async Task<IReadOnlyList<Device>> RegisterAsync(Guid eventId, string organizer, CancellationToken cancellationToken)
    {
        var devices = new ConcurrentBag<Device>();
        await Parallel.ForEachAsync(Enumerable.Range(1, options.Devices), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (number, token) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BackOfficeApi, $"events/{eventId}/devices"))
            {
                Content = JsonContent.Create(new { name = $"T8 door {number:000}" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", organizer);
            using var response = await _http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                throw new SimulationException($"Registering a device failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(token)}");
            }

            var registered = (await response.Content.ReadFromJsonAsync<RegisteredDevice>(Json, token))!;
            devices.Add(new Device(registered.DeviceId, eventId, registered.ClientId, registered.ClientSecret));
        });

        Console.WriteLine($"Registered {devices.Count} devices.");
        return [.. devices];
    }

    private async Task RevokeAsync(Guid eventId, string organizer, IReadOnlyList<Device> devices)
    {
        await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (device, token) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(options.BackOfficeApi, $"events/{eventId}/devices/{device.DeviceId}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", organizer);
            using var _ = await _http.SendAsync(request, token);
        });
        Console.WriteLine($"Revoked the {devices.Count} devices again.");
    }

    private async Task SignInAsync(Device device, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(options.CheckInApi, "device-token"), new { device.ClientId, device.ClientSecret }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<DeviceToken>(Json, cancellationToken))!;
        (device.Token, device.TokenExpiresAt) = (token.AccessToken, time.GetUtcNow().AddSeconds(token.ExpiresIn));
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path, Device device, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(options.CheckInApi, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private async Task<string> UserTokenAsync(string user, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "eventticketing-dev",
            ["username"] = user,
            ["password"] = user,
        });
        using var response = await _http.PostAsync(new Uri(options.Keycloak, "realms/eventticketing/protocol/openid-connect/token"), form, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SimulationException($"{user} can't sign in at {options.Keycloak}: is the system running? {(int)response.StatusCode}");
        }

        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("access_token").GetString()!;
    }

    private async Task<Guid> EventIdAsync(CancellationToken cancellationToken)
    {
        var page = await _http.GetFromJsonAsync<JsonElement>(new Uri(options.PublicApi, $"events?q={Uri.EscapeDataString($"\"{options.EventName}\"")}"), cancellationToken);
        return page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("eventId").GetGuid()).FirstOrDefault() is { } id && id != Guid.Empty
            ? id
            : throw new SimulationException($"No event called {options.EventName}: run the seeder (or lab reset) first.");
    }

    private sealed class Device(Guid deviceId, Guid eventId, string clientId, string clientSecret)
    {
        public Guid DeviceId { get; } = deviceId;

        public Guid EventId { get; } = eventId;

        public string ClientId { get; } = clientId;

        public string ClientSecret { get; } = clientSecret;

        public string Token { get; set; } = "";

        public DateTimeOffset TokenExpiresAt { get; set; }

        public long Cursor { get; set; }

        public HashSet<Guid> Issued { get; } = [];

        public HashSet<Guid> Revoked { get; } = [];
    }

    private sealed record RegisteredDevice(Guid DeviceId, string ClientId, string ClientSecret);

    private sealed record DeviceToken(string AccessToken, int ExpiresIn);

    private sealed record DoorListPage(long Cursor, bool More, List<Guid> Issued, List<Guid> Revoked, List<JsonElement> Admitted);
}

/// <summary>What a device's request was for: the first full download, a sync's changes, or an upload.</summary>
public enum Kind
{
    Download,
    Sync,
    Upload,
}

/// <param name="Problem">Why it failed: an HTTP status, or no answer.</param>
/// <param name="At">When the answer came (or didn't).</param>
public sealed record Sample(Kind Kind, TimeSpan Took, bool Ok, string? Problem = null, DateTimeOffset At = default);

public sealed record RevocationProbe(Guid TicketId, long RevokedAt);
