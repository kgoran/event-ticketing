using System.Collections.Concurrent;
using EventTicketing.CheckIn.Application;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>Keycloak's device clients as CheckIn sees them through its port (D61): created and deleted, nothing more.</summary>
internal sealed class FakeDeviceDirectory : IDeviceDirectory
{
    private readonly ConcurrentDictionary<string, DeviceCredentials> _clients = new();

    public bool Available => true;

    public Uri? TokenEndpoint { get; } = new("http://keycloak.test/realms/eventticketing/protocol/openid-connect/token");

    public Task CreateAsync(DeviceCredentials device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        _clients[device.ClientId] = device;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string clientId, CancellationToken cancellationToken)
    {
        _clients.TryRemove(clientId, out _);
        return Task.CompletedTask;
    }

    public DeviceCredentials? Client(string clientId) => _clients.GetValueOrDefault(clientId);
}
