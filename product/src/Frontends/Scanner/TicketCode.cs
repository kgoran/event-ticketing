using System.Buffers.Text;
using System.Text.Json;

namespace EventTicketing.Scanner;

/// <summary>
/// What a ticket's QR code says (ADR-0051): a compact JWS whose payload is the ticket, its event
/// and its version. Reading it proves nothing; the signature, checked against the event's public
/// key, does (<see cref="DoorBrowser.VerifyAsync"/>).
/// </summary>
public sealed record TicketCode(Guid TicketId, Guid EventId, int Version)
{
    /// <summary>The code's claims, or null if it isn't a ticket code at all.</summary>
    public static TicketCode? Parse(string? code)
    {
        var parts = code?.Trim().Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var root = payload.RootElement;
            return Guid.TryParse(root.GetProperty("tid").GetString(), out var ticketId) && Guid.TryParse(root.GetProperty("eid").GetString(), out var eventId)
                ? new TicketCode(ticketId, eventId, root.TryGetProperty("ver", out var version) ? version.GetInt32() : 1)
                : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
