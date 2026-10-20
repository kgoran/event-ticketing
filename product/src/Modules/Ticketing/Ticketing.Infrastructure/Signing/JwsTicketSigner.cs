using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventTicketing.Ticketing.Application;
using EventTicketing.Ticketing.Domain;

namespace EventTicketing.Ticketing.Infrastructure.Signing;

/// <summary>
/// A ticket's code is a JWS in compact form, signed with ES256 (ADR-0051):
/// <c>base64url(header) . base64url(payload) . base64url(signature)</c>. The header names the key
/// (<c>kid</c>); the payload holds the ticket ID, the event ID and the version, and nothing
/// personal. It is a standard JWS, so any JOSE library can check it with the event's public key.
/// </summary>
internal sealed class JwsTicketSigner(MasterKey masterKey) : ITicketSigner
{
    public string Sign(TicketSpec ticket, EventKey key)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(key);

        var header = JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT", kid = key.KeyId });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { tid = ticket.TicketId, eid = ticket.EventId, ver = ticket.Version });
        var signingInput = $"{Base64Url.EncodeToString(header)}.{Base64Url.EncodeToString(payload)}";

        var privateKey = masterKey.Decrypt(key.EncryptedPrivateKey, key.KeyId);
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(privateKey, out _);

            // .NET signs in IEEE P1363 form (r ‖ s, 64 bytes for P-256), which is what JWS expects.
            var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
            return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}
