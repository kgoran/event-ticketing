namespace EventTicketing.Inventory.Domain;

/// <summary>
/// When an event's places may be held: from <see cref="OnSaleAt"/> until the event starts. Catalog
/// owns the schedule; this is Inventory's copy, built from EventPublished and OnSaleScheduled. The
/// claim compares it with the database clock (design-decisions C1-e).
/// </summary>
public sealed class SalesWindow
{
    private SalesWindow()
    {
    }

    public Guid EventId { get; private set; }

    /// <summary>Null until the organizer schedules sales.</summary>
    public DateTimeOffset? OnSaleAt { get; private set; }

    /// <summary>The event's start; null only while OnSaleScheduled has overtaken EventPublished.</summary>
    public DateTimeOffset? SalesEndAt { get; private set; }

    /// <summary>When Catalog last set the on-sale time, so a message that arrives late can't undo a newer one.</summary>
    public DateTimeOffset? OnSaleSetAt { get; private set; }

    public static SalesWindow For(Guid eventId) => new() { EventId = eventId };

    public void EndSalesAt(DateTimeOffset startsAt) => SalesEndAt = startsAt.ToUniversalTime();

    /// <summary>Applies an on-sale time Catalog set at <paramref name="setAt"/>, unless a newer one is already here.</summary>
    public void ScheduleOnSale(DateTimeOffset? onSaleAt, DateTimeOffset setAt)
    {
        if (OnSaleSetAt is { } current && current >= setAt)
        {
            return;
        }

        OnSaleAt = onSaleAt?.ToUniversalTime();
        OnSaleSetAt = setAt.ToUniversalTime();
    }
}
