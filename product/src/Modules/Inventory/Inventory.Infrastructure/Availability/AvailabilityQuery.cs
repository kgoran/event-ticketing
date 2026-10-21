using EventTicketing.Inventory.Infrastructure.Endpoints;
using EventTicketing.Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Inventory.Infrastructure.Availability;

/// <summary>
/// An event's free places, from the seat rows themselves: what a snapshot is built from, and what
/// the availability endpoint asks when there is no snapshot. Uses the database clock like the claim,
/// but it is still only advice: by the time a fan asks for a seat, someone else may have it.
/// </summary>
internal static class AvailabilityQuery
{
    public static async Task<Endpoints.Availability> RunAsync(InventoryDbContext db, Guid eventId, CancellationToken cancellationToken)
    {
        // DateTimeOffset.UtcNow inside a query is translated to now(), the database clock.
        var free = db.Seats.AsNoTracking().Where(seat => seat.EventId == eventId
            && (seat.Status == SeatStatus.Available || (seat.Status == SeatStatus.Held && seat.HeldUntil < DateTimeOffset.UtcNow)));

        var sections = await db.Seats.AsNoTracking()
            .Where(seat => seat.EventId == eventId)
            .GroupBy(seat => seat.Section)
            .OrderBy(section => section.Key)
            .Select(section => new SectionAvailability(
                section.Key,
                section.Count(seat => seat.Status == SeatStatus.Available || (seat.Status == SeatStatus.Held && seat.HeldUntil < DateTimeOffset.UtcNow)),
                section.Count()))
            .ToListAsync(cancellationToken);

        var seatIds = await free.Where(seat => seat.Row != null).Select(seat => seat.SeatId).ToListAsync(cancellationToken);

        var onSale = await db.SalesWindows.AnyAsync(
            w => w.EventId == eventId && w.OnSaleAt <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow < w.SalesEndAt,
            cancellationToken);

        return new Endpoints.Availability(eventId, onSale, sections, seatIds);
    }

    /// <summary>The events on sale now, whose snapshots are kept up to date.</summary>
    public static Task<List<Guid>> OnSaleAsync(InventoryDbContext db, CancellationToken cancellationToken) =>
        db.SalesWindows.AsNoTracking()
            .Where(w => w.OnSaleAt <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow < w.SalesEndAt)
            .Select(w => w.EventId)
            .ToListAsync(cancellationToken);
}
