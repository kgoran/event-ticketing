namespace EventTicketing.Ticketing.Domain;

/// <summary>
/// One seat's (or standing place's) ticket for a confirmed order. Its <see cref="Code"/> is what
/// the QR code holds: a JWS signed with the event's key over the ticket ID, the event ID and the
/// ticket's <see cref="Version"/> (ADR-0051). A new version would replace the code, e.g. after a
/// reissue; M1b only issues version 1.
/// </summary>
public sealed class Ticket
{
    private Ticket()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid EventId { get; private set; }

    /// <summary>The seat, or the standing slot, the ticket is for. Unique per order: a ticket per place.</summary>
    public Guid SeatId { get; private set; }

    /// <summary>The fan who bought it (the token's subject).</summary>
    public string CustomerId { get; private set; } = string.Empty;

    public string Label { get; private set; } = string.Empty;

    public int Version { get; private set; }

    /// <summary>The key the code is signed with, so keys can rotate without breaking issued tickets.</summary>
    public string KeyId { get; private set; } = string.Empty;

    public string Code { get; private set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; private set; }

    public static Ticket Issue(TicketSpec spec, string keyId, string code, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return new Ticket
        {
            Id = spec.TicketId,
            OrderId = spec.OrderId,
            EventId = spec.EventId,
            SeatId = spec.SeatId,
            CustomerId = spec.CustomerId,
            Label = spec.Label,
            Version = spec.Version,
            KeyId = keyId,
            Code = code,
            IssuedAt = now.ToUniversalTime(),
        };
    }
}

/// <summary>What a ticket will say, before it is signed: the signer needs the ID and version first.</summary>
public sealed record TicketSpec(Guid TicketId, Guid OrderId, Guid EventId, Guid SeatId, string CustomerId, string Label, int Version = 1);

/// <summary>
/// Ticketing's own copy of what it needs about an event (ADR-0007): its name and time for the
/// tickets, and a label for every place. Built from Catalog's EventPublished.
/// </summary>
public sealed record TicketedEvent(Guid EventId, string Name, DateTimeOffset StartsAt, string TimeZone);

/// <summary>A seat's label, e.g. "A · Row 3 · Seat 12".</summary>
public sealed record SeatLabel(Guid EventId, Guid SeatId, string Label);

/// <summary>
/// A standing area, by its price category: Inventory makes up the standing slots' IDs, so a slot is
/// recognised by the price category the order carries for it.
/// </summary>
public sealed record StandingLabel(Guid EventId, Guid PriceCategoryId, string Label);

/// <summary>
/// An event's signing key pair (ADR-0051). The private key is stored only encrypted with the
/// master key (envelope encryption); the public key is published so anyone can verify a ticket.
/// </summary>
/// <param name="PublicKey">SubjectPublicKeyInfo (DER).</param>
/// <param name="EncryptedPrivateKey">PKCS#8 (DER), encrypted with AES-GCM under the master key.</param>
public sealed record EventKey(Guid EventId, string KeyId, byte[] PublicKey, byte[] EncryptedPrivateKey, DateTimeOffset CreatedAt);
