namespace EventTicketing.Notifications;

public enum DeliveryStatus
{
    /// <summary>Recorded; the email goes out when its SendEmail message runs.</summary>
    Pending,
    Sent,
}

/// <summary>Which email a delivery is, and what it is about.</summary>
public static class EmailKinds
{
    /// <summary>The fan's tickets for an order, with a QR code per ticket.</summary>
    public const string Tickets = "tickets";

    /// <summary>A paid order's money went back to the fan.</summary>
    public const string Refund = "refund";
}

/// <summary>
/// The delivery log (ADR-0052): one row per email, unique per kind and order, so an event delivered
/// again can't send it twice. The row is written in the consumer's transaction; the email goes out
/// afterwards, outside any transaction, and then the row is marked sent. A crash between sending
/// and marking sends a second copy: at least once, like every message here, and visible in the log.
/// </summary>
public sealed class Delivery
{
    private Delivery()
    {
    }

    public Guid Id { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    /// <summary>What the email is about: the order.</summary>
    public Guid OrderId { get; private set; }

    public string Recipient { get; private set; } = string.Empty;

    /// <summary>The template's model, as JSON: the email can be rendered again from the row alone.</summary>
    public string Model { get; private set; } = string.Empty;

    public DeliveryStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public static Delivery Queue(string kind, Guid orderId, string recipient, string model, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = kind,
        OrderId = orderId,
        Recipient = recipient,
        Model = model,
        Status = DeliveryStatus.Pending,
        CreatedAt = now.ToUniversalTime(),
    };

    /// <summary>Returns false if it was already sent (a repeated send).</summary>
    public bool MarkSent(DateTimeOffset now)
    {
        if (Status == DeliveryStatus.Sent)
        {
            return false;
        }

        Status = DeliveryStatus.Sent;
        SentAt = now.ToUniversalTime();
        return true;
    }
}

/// <summary>The "your tickets" email's model.</summary>
public sealed record TicketsEmailModel(Guid OrderId, string EventName, DateTimeOffset StartsAt, string TimeZone, IReadOnlyList<TicketLine> Tickets);

public sealed record TicketLine(Guid TicketId, string Label, string Code);

/// <summary>The "order refunded" email's model.</summary>
/// <param name="Reason">SeatsLost or PaidAfterClose.</param>
public sealed record RefundEmailModel(Guid OrderId, string EventName, long AmountMinorUnits, string Currency, string Reason);
