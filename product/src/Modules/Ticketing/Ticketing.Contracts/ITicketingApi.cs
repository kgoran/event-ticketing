namespace EventTicketing.Ticketing.Contracts;

/// <summary>
/// Ticketing's module API (ADR-0007). CheckIn calls it for the event's public keys, which go to the
/// doors with the door list (ADR-0013). Designed as if it were remote: plain data, safe to repeat.
/// </summary>
public interface ITicketingApi
{
    /// <summary>The public keys that verify the event's ticket codes; null if the event has none yet.</summary>
    Task<TicketKeySet?> KeysAsync(Guid eventId, CancellationToken cancellationToken);
}

/// <summary>A JSON Web Key Set (RFC 7517): the public keys that verify an event's ticket codes.</summary>
public sealed record TicketKeySet(IReadOnlyList<TicketKey> Keys);

/// <summary>An EC public key as a JWK: P-256, for ES256 signatures, named by <paramref name="Kid"/>.</summary>
public sealed record TicketKey(string Kty, string Crv, string X, string Y, string Kid, string Alg, string Use);
