using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.CheckIn.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketing.CheckIn.Infrastructure.Identity;

/// <summary>Configuration section "CheckIn:Keycloak". Without a client secret, devices can't be registered.</summary>
public sealed class CheckInKeycloakOptions
{
    /// <summary>Keycloak's address as the host reaches it, e.g. http://localhost:8180.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Keycloak's address as the devices reach it, if not <see cref="BaseUrl"/>: the token endpoint they are given.</summary>
    public Uri? PublicUrl { get; set; }

    public string Realm { get; set; } = "eventticketing";

    /// <summary>A service account allowed to manage clients, and nothing else (D70).</summary>
    public string ClientId { get; set; } = "eventticketing-checkin";

    public string? ClientSecret { get; set; }

    /// <summary>The API audience every device token must carry.</summary>
    public string Audience { get; set; } = "eventticketing-api";
}

/// <summary>
/// A Keycloak client per device (D61), through Keycloak's admin REST API with CheckIn's own service
/// account. Its tokens carry fixed claims: the role door-staff, the event and the device. So the
/// service account needs to manage clients only, never users' roles (D70).
/// </summary>
internal sealed partial class KeycloakDevices(
    HttpClient http,
    CheckInAdminToken token,
    IOptions<CheckInKeycloakOptions> options,
    ILogger<KeycloakDevices> logger)
    : IDeviceDirectory
{
    private CheckInKeycloakOptions Settings => options.Value;

    public bool Available => true;

    public Uri? TokenEndpoint => new(Settings.PublicUrl ?? Settings.BaseUrl!, $"realms/{Settings.Realm}/protocol/openid-connect/token");

    public async Task CreateAsync(DeviceCredentials device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        using var request = await AdminRequestAsync(HttpMethod.Post, "clients", cancellationToken);
        request.Content = JsonContent.Create(new
        {
            clientId = device.ClientId,
            name = device.Name,
            description = $"Door device for event {device.EventId}",
            enabled = true,
            protocol = "openid-connect",
            publicClient = false,
            clientAuthenticatorType = "client-secret",
            secret = device.ClientSecret,
            serviceAccountsEnabled = true,
            standardFlowEnabled = false,
            implicitFlowEnabled = false,
            directAccessGrantsEnabled = false,
            fullScopeAllowed = false,
            protocolMappers = new object[]
            {
                new
                {
                    name = "audience",
                    protocol = "openid-connect",
                    protocolMapper = "oidc-audience-mapper",
                    config = new Dictionary<string, string> { ["included.client.audience"] = Settings.Audience, ["access.token.claim"] = "true", ["id.token.claim"] = "false" },
                },
                Claim("roles", "door-staff"),
                Claim("event_id", device.EventId.ToString()),
                Claim("device_id", device.DeviceId.ToString()),
            },
        });

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        LogCreated(logger, device.ClientId, device.EventId);
    }

    public async Task DeleteAsync(string clientId, CancellationToken cancellationToken)
    {
        if (await ClientUuidAsync(clientId, cancellationToken) is not { } uuid)
        {
            return; // already gone
        }

        using var request = await AdminRequestAsync(HttpMethod.Delete, $"clients/{uuid}", cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }

        LogDeleted(logger, clientId);
    }

    /// <summary>A claim with a fixed value in every access token the client gets.</summary>
    private static object Claim(string name, string value) => new
    {
        name,
        protocol = "openid-connect",
        protocolMapper = "oidc-hardcoded-claim-mapper",
        config = new Dictionary<string, string>
        {
            ["claim.name"] = name,
            ["claim.value"] = value,
            ["jsonType.label"] = "String",
            ["access.token.claim"] = "true",
            ["id.token.claim"] = "false",
            ["userinfo.token.claim"] = "false",
        },
    };

    private async Task<string?> ClientUuidAsync(string clientId, CancellationToken cancellationToken)
    {
        using var request = await AdminRequestAsync(HttpMethod.Get, $"clients?clientId={Uri.EscapeDataString(clientId)}", cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var clients = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return clients.EnumerateArray().Select(client => client.GetProperty("id").GetString()).FirstOrDefault();
    }

    private async Task<HttpRequestMessage> AdminRequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(Settings.BaseUrl!, $"admin/realms/{Settings.Realm}/{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token.GetAsync(http, cancellationToken));
        return request;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Keycloak: client {ClientId} created for a door device of event {EventId}")]
    private static partial void LogCreated(ILogger logger, string clientId, Guid eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Keycloak: client {ClientId} deleted, its device revoked")]
    private static partial void LogDeleted(ILogger logger, string clientId);
}

/// <summary>CheckIn's service-account token, shared and reused until shortly before it expires.</summary>
internal sealed class CheckInAdminToken(IOptions<CheckInKeycloakOptions> options, TimeProvider time)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Value, DateTimeOffset ExpiresAt)? _token;

    private CheckInKeycloakOptions Settings => options.Value;

    public async Task<string> GetAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } cached && cached.ExpiresAt - time.GetUtcNow() > TimeSpan.FromSeconds(30))
            {
                return cached.Value;
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = Settings.ClientId,
                ["client_secret"] = Settings.ClientSecret!,
            });
            using var response = await http.PostAsync(new Uri(Settings.BaseUrl!, $"realms/{Settings.Realm}/protocol/openid-connect/token"), form, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var value = body.GetProperty("access_token").GetString()!;
            _token = (value, time.GetUtcNow().AddSeconds(body.GetProperty("expires_in").GetInt32()));
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>When no identity provider is configured (module tests, a host run on its own): no devices.</summary>
internal sealed class UnconfiguredDevices : IDeviceDirectory
{
    public bool Available => false;

    public Uri? TokenEndpoint => null;

    public Task CreateAsync(DeviceCredentials device, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No identity provider is configured for devices (CheckIn:Keycloak).");

    public Task DeleteAsync(string clientId, CancellationToken cancellationToken) => Task.CompletedTask;
}
