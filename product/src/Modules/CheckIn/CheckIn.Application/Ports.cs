using EventTicketing.CheckIn.Domain;

namespace EventTicketing.CheckIn.Application;

/// <summary>
/// An event's door list (ADR-0013). Every write takes the event's lock first and keeps it until the
/// transaction ends, so an event's changes commit in the order of their sequence numbers: a device
/// that has read up to one never misses an earlier one committed after it (D71).
/// </summary>
public interface IDoorList
{
    Task AddAsync(Guid eventId, IEnumerable<Guid> ticketIds, DoorListChange change, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The ticket's first admission: true if this is it, false if it was admitted before.</summary>
    Task<bool> TryAdmitAsync(Guid eventId, Guid ticketId, Guid deviceId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>When and where the ticket was first let in; null if it never was.</summary>
    Task<(Guid DeviceId, DateTimeOffset At)?> FirstAdmissionAsync(Guid ticketId, CancellationToken cancellationToken);

    /// <summary>Holds the event's door-list lock until the transaction ends; safe to repeat.</summary>
    Task LockAsync(Guid eventId, CancellationToken cancellationToken);
}

public interface IScanLog
{
    Task<Scan?> FindAsync(Guid scanId, CancellationToken cancellationToken);

    void Add(Scan scan);
}

public interface IDevices
{
    void Add(Device device);

    /// <summary>The device, tracked so a change is saved with the command.</summary>
    Task<Device?> FindAsync(Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>CheckIn's copy of the events it has door lists for.</summary>
public interface ICheckInEvents
{
    Task<CheckInEvent?> FindAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>Stores the event once; a repeated call changes nothing.</summary>
    Task AddAsync(CheckInEvent checkInEvent, CancellationToken cancellationToken);
}

/// <summary>
/// The identity provider's side of devices (D61): a client per device, which signs in with client
/// credentials and whose tokens carry the role door-staff, the event and the device.
/// </summary>
public interface IDeviceDirectory
{
    /// <summary>False when no identity provider is configured: devices can't be registered.</summary>
    bool Available { get; }

    /// <summary>Where a device asks for its tokens, as the device reaches it.</summary>
    Uri? TokenEndpoint { get; }

    Task CreateAsync(DeviceCredentials device, CancellationToken cancellationToken);

    /// <summary>Deletes the device's client: safe to repeat.</summary>
    Task DeleteAsync(string clientId, CancellationToken cancellationToken);
}

/// <summary>A new device's client: its secret is shown once, to whoever registers it, and stored nowhere else.</summary>
public sealed record DeviceCredentials(Guid DeviceId, Guid EventId, string Name, string ClientId, string ClientSecret);
