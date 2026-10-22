using EventTicketing.Shared.Domain;

namespace EventTicketing.CheckIn.Domain;

/// <summary>
/// A scanner registered for one event's doors (ADR-0013, D61). It signs in to Keycloak as a client
/// of its own, whose tokens name the device and the event. Revoking it deletes the client, and
/// CheckIn refuses the device at once, whatever token it still holds.
/// </summary>
public sealed class Device
{
    private Device()
    {
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Its client in Keycloak (<see cref="ClientIdFor"/>).</summary>
    public string ClientId { get; private set; } = string.Empty;

    /// <summary>Who registered it: the token's subject.</summary>
    public string RegisteredBy { get; private set; } = string.Empty;

    public DateTimeOffset RegisteredAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    public static string ClientIdFor(Guid deviceId) => "device-" + deviceId.ToString("N");

    public static Device Register(Guid id, Guid eventId, string name, string registeredBy, DateTimeOffset now) => new()
    {
        Id = id,
        EventId = eventId,
        Name = name,
        ClientId = ClientIdFor(id),
        RegisteredBy = registeredBy,
        RegisteredAt = now.ToUniversalTime(),
    };

    public Result<Unit> Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return Error.Conflict("device.already_revoked", "The device is already revoked.");
        }

        RevokedAt = now.ToUniversalTime();
        return Unit.Value;
    }
}
