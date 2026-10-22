namespace EventTicketing.Scanner;

/// <summary>What the door knows, from the door list and from its own scans.</summary>
public interface IDoorView
{
    Guid EventId { get; }

    bool IsIssued(Guid ticketId);

    bool IsRevoked(Guid ticketId);

    /// <summary>When the ticket was let in, here or at another door that has synced; null if never.</summary>
    DateTimeOffset? AdmittedAt(Guid ticketId);
}

/// <summary>Let in or not, why, and what to tell the person at the door.</summary>
/// <param name="Reason">For the scan log: "ok", "not-on-list-yet", "bad-code", "other-event", "revoked", "already-scanned".</param>
public sealed record Decision(bool Admit, string Reason, string Message);

/// <summary>
/// The door's rule (ADR-0013), offline: a valid signature from this event's key, not revoked, not
/// already let in. A valid ticket that isn't on the list yet was bought after the last sync, and
/// the door lets it in rather than stop the queue: the clearest contrast with Inventory, which
/// refuses a sale rather than risk selling a seat twice.
/// </summary>
public static class DoorDecision
{
    public static Decision Decide(TicketCode? code, bool signatureValid, IDoorView door)
    {
        ArgumentNullException.ThrowIfNull(door);

        if (code is null || !signatureValid)
        {
            return new Decision(false, "bad-code", code is null ? "Not a ticket code." : "Not signed by this event's key: a forged or damaged code.");
        }

        if (code.EventId != door.EventId)
        {
            return new Decision(false, "other-event", "A ticket for another event.");
        }

        if (door.IsRevoked(code.TicketId))
        {
            return new Decision(false, "revoked", "Revoked: this ticket no longer lets anyone in.");
        }

        if (door.AdmittedAt(code.TicketId) is { } at)
        {
            return new Decision(false, "already-scanned", $"Already let in at {at.ToLocalTime():HH:mm:ss}.");
        }

        return door.IsIssued(code.TicketId)
            ? new Decision(true, "ok", "Welcome.")
            : new Decision(true, "not-on-list-yet", "Welcome: a new ticket, bought after the last sync.");
    }
}
