using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace EventTicketing.Ticketing.Infrastructure.Signing;

public sealed class TicketingOptions
{
    /// <summary>
    /// The secret every event's private key is encrypted under (ADR-0051), at least 32 characters.
    /// Locally an Aspire parameter generated per run; in the lab a Kubernetes Secret. Only the hosts
    /// that sign tickets (Workers) need it.
    /// </summary>
    public string? MasterKey { get; set; }
}

/// <summary>
/// Envelope encryption (ADR-0051): the master key never touches the database; it encrypts the
/// per-event private keys that do. AES-GCM, so a changed ciphertext fails to decrypt instead of
/// yielding a wrong key, and the key ID as associated data, so a ciphertext can't be moved to
/// another key's row. A cloud key vault would take this class's place.
/// </summary>
internal sealed class MasterKey(IOptions<TicketingOptions> options)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Lazy<byte[]> _key = new(() => Derive(options.Value.MasterKey));

    /// <returns>nonce ‖ ciphertext ‖ tag.</returns>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, string keyId)
    {
        var sealedBox = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = sealedBox.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Encrypt(nonce, plaintext, sealedBox.AsSpan(NonceSize, plaintext.Length), sealedBox.AsSpan(NonceSize + plaintext.Length), Encoding.UTF8.GetBytes(keyId));
        return sealedBox;
    }

    /// <exception cref="AuthenticationTagMismatchException">Wrong master key, wrong key ID, or a changed ciphertext.</exception>
    public byte[] Decrypt(ReadOnlySpan<byte> sealedBox, string keyId)
    {
        var plaintext = new byte[sealedBox.Length - NonceSize - TagSize];

        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Decrypt(sealedBox[..NonceSize], sealedBox[NonceSize..^TagSize], sealedBox[^TagSize..], plaintext, Encoding.UTF8.GetBytes(keyId));
        return plaintext;
    }

    /// <summary>The configured secret is text of any shape; HKDF turns it into a 256-bit AES key.</summary>
    private static byte[] Derive(string? secret)
    {
        if (secret is not { Length: >= 32 })
        {
            throw new InvalidOperationException("Ticketing:MasterKey must be set to a secret of at least 32 characters to sign tickets (ADR-0051).");
        }

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, info: "EventTicketing.Ticketing.EventKeys"u8.ToArray());
    }
}
