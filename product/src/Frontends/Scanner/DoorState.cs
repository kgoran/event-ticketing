namespace EventTicketing.Scanner;

/// <summary>The device's credentials, as BackOfficeApi showed them when it was registered (D61).</summary>
public sealed record DeviceSetup(string ClientId, string ClientSecret);

/// <summary>A public key that verifies ticket codes (a JWK, as the door list carries it).</summary>
public sealed record TicketKey(string Kty, string Crv, string X, string Y, string Kid, string Alg, string Use);

/// <summary>A scan waiting to be uploaded: the scanner makes its ID, so an upload is safe to repeat.</summary>
public sealed record PendingScan(Guid ScanId, Guid TicketId, DateTimeOffset ScannedAt, bool Admitted, string? Reason);

/// <summary>The door list as the device last downloaded it, merged change by change: written after every sync.</summary>
public sealed class DoorList
{
    public Guid EventId { get; set; }

    public string? EventName { get; set; }

    public long Cursor { get; set; }

    public List<TicketKey> Keys { get; set; } = [];

    public HashSet<Guid> Issued { get; set; } = [];

    public HashSet<Guid> Revoked { get; set; } = [];

    /// <summary>Tickets let in at any door, as the server knows them.</summary>
    public Dictionary<Guid, DateTimeOffset> Admitted { get; set; } = [];

    public DateTimeOffset? SyncedAt { get; set; }
}

/// <summary>What this door did since its last upload: written after every scan, small.</summary>
public sealed class DoorQueue
{
    public List<PendingScan> Pending { get; set; } = [];

    /// <summary>Tickets this door let in, so a second scan here is refused even offline.</summary>
    public Dictionary<Guid, DateTimeOffset> AdmittedHere { get; set; } = [];
}

/// <summary>The door list and this door's own scans, as the decision sees them.</summary>
public sealed class DoorView(DoorList list, DoorQueue queue) : IDoorView
{
    public Guid EventId => list.EventId;

    public bool IsIssued(Guid ticketId) => list.Issued.Contains(ticketId);

    public bool IsRevoked(Guid ticketId) => list.Revoked.Contains(ticketId);

    public DateTimeOffset? AdmittedAt(Guid ticketId) =>
        queue.AdmittedHere.TryGetValue(ticketId, out var here) ? here
        : list.Admitted.TryGetValue(ticketId, out var elsewhere) ? elsewhere
        : null;
}
