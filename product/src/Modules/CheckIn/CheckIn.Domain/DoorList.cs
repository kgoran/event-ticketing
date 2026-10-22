namespace EventTicketing.CheckIn.Domain;

/// <summary>What the door list learnt about a ticket.</summary>
public enum DoorListChange
{
    /// <summary>Issued: the ticket lets its holder in (TicketsIssued).</summary>
    Issued = 1,

    /// <summary>Revoked: it no longer does (TicketsRevoked).</summary>
    Revoked = 2,

    /// <summary>Admitted at a door: a second scan anywhere is a duplicate.</summary>
    Admitted = 3,
}

/// <summary>
/// One change to an event's door list (ADR-0013). Devices download the list once, then only the
/// changes after the last <see cref="Sequence"/> they have: their cursor. A ticket has each kind
/// of change at most once, so its first admission is the one that counts.
/// </summary>
public sealed class DoorListEntry
{
    private DoorListEntry()
    {
    }

    public long Sequence { get; private set; }

    public Guid EventId { get; private set; }

    public Guid TicketId { get; private set; }

    public DoorListChange Change { get; private set; }

    public DateTimeOffset At { get; private set; }

    /// <summary>For an admission, the device that let the ticket in.</summary>
    public Guid? DeviceId { get; private set; }

    public static DoorListEntry For(Guid eventId, Guid ticketId, DoorListChange change, DateTimeOffset at) => new()
    {
        EventId = eventId,
        TicketId = ticketId,
        Change = change,
        At = at.ToUniversalTime(),
    };
}

/// <summary>How the server saw an uploaded scan.</summary>
public enum ScanOutcome
{
    /// <summary>The ticket's first admission: it counts.</summary>
    Admitted = 1,

    /// <summary>The device let the ticket in, but it had been admitted before: DuplicateEntryDetected.</summary>
    Duplicate = 2,

    /// <summary>The device turned it away (revoked, unknown, a bad code): logged.</summary>
    Refused = 3,
}

/// <summary>
/// A scan as a device uploaded it (ADR-0013): decided at the door, offline if need be, and sent in
/// batches. Its ID, made by the device, makes an upload safe to repeat.
/// </summary>
public sealed class Scan
{
    private Scan()
    {
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    public Guid DeviceId { get; private set; }

    public Guid TicketId { get; private set; }

    public DateTimeOffset ScannedAt { get; private set; }

    /// <summary>What the device decided at the door.</summary>
    public bool Admitted { get; private set; }

    /// <summary>Why the device turned it away, in its words ("revoked", "unknown", …).</summary>
    public string? Reason { get; private set; }

    public ScanOutcome Outcome { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public static Scan Record(
        Guid id, Guid eventId, Guid deviceId, Guid ticketId, DateTimeOffset scannedAt, bool admitted, string? reason, ScanOutcome outcome, DateTimeOffset now) => new()
    {
        Id = id,
        EventId = eventId,
        DeviceId = deviceId,
        TicketId = ticketId,
        ScannedAt = scannedAt.ToUniversalTime(),
        Admitted = admitted,
        Reason = reason,
        Outcome = outcome,
        ReceivedAt = now.ToUniversalTime(),
    };
}

/// <summary>CheckIn's copy of an event (ADR-0007): who organizes it, and its name for the devices.</summary>
public sealed record CheckInEvent(Guid EventId, Guid OrganizerId, string Name, DateTimeOffset StartsAt);
