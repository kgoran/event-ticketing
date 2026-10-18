using EventTicketing.Inventory.Application;
using EventTicketing.Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Inventory.Infrastructure.Persistence;

/// <summary>
/// The seat statements from ADR-0011. Each runs inside the command's transaction, takes row locks for
/// one statement only, and never waits on another claim (<c>SKIP LOCKED</c>): a seat someone else is
/// claiming at that moment simply counts as unavailable. Every expiry check uses the database clock.
/// </summary>
internal sealed class SeatInventory(InventoryDbContext db) : ISeatInventory
{
    public Task AddSeatsAsync(Guid eventId, IReadOnlyList<NewSeat> seats, CancellationToken cancellationToken)
    {
        db.Seats.AddRange(seats.Select(seat => new SeatUnit
        {
            EventId = eventId,
            SeatId = seat.SeatId,
            Section = seat.Section,
            Row = seat.Row,
            Number = seat.Number,
            PriceCategoryId = seat.PriceCategoryId,
            Status = SeatStatus.Available,
        }));

        return Task.CompletedTask;
    }

    public async Task<bool> TryClaimAsync(
        Guid eventId,
        IReadOnlyCollection<Guid> seatIds,
        Guid holdId,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var requested = seatIds.ToArray();
        var claimed = await db.Database.ExecuteSqlAsync(
            $"""
            WITH claimable AS (
                SELECT seat_id FROM inventory.seats
                WHERE event_id = {eventId} AND seat_id = ANY({requested})
                  AND (status = 'Available' OR (status = 'Held' AND held_until < now()))
                ORDER BY seat_id
                FOR UPDATE SKIP LOCKED)
            UPDATE inventory.seats AS s
            SET status = 'Held', hold_id = {holdId}, held_until = now() + make_interval(secs => {lifetime.TotalSeconds}), order_id = NULL
            FROM claimable
            WHERE s.event_id = {eventId} AND s.seat_id = claimable.seat_id
            """,
            cancellationToken);

        return claimed == requested.Length;
    }

    public async Task<bool> TryExtendAsync(Guid holdId, int seatCount, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var extended = await db.Database.ExecuteSqlAsync(
            $"""
            WITH ours AS (
                SELECT seat_id FROM inventory.seats
                WHERE hold_id = {holdId} AND status = 'Held' AND held_until > now()
                FOR UPDATE)
            UPDATE inventory.seats AS s
            SET held_until = now() + make_interval(secs => {lifetime.TotalSeconds})
            FROM ours
            WHERE s.seat_id = ours.seat_id AND s.hold_id = {holdId}
              AND (SELECT count(*) FROM ours) = {seatCount}
            """,
            cancellationToken);

        return extended == seatCount;
    }

    public async Task<bool> TrySellAsync(Guid holdId, Guid orderId, int seatCount, CancellationToken cancellationToken)
    {
        // No expiry check: if nobody has taken the seats since the hold expired, they're still ours.
        var sold = await db.Database.ExecuteSqlAsync(
            $"""
            WITH ours AS (
                SELECT seat_id FROM inventory.seats
                WHERE hold_id = {holdId} AND status = 'Held'
                FOR UPDATE)
            UPDATE inventory.seats AS s
            SET status = 'Sold', order_id = {orderId}, held_until = NULL
            FROM ours
            WHERE s.seat_id = ours.seat_id AND s.hold_id = {holdId}
              AND (SELECT count(*) FROM ours) = {seatCount}
            """,
            cancellationToken);

        return sold == seatCount;
    }

    public async Task<IReadOnlyList<SeatOfHold>> SeatsOfHoldAsync(Guid holdId, CancellationToken cancellationToken) =>
        await db.Seats
            .Where(seat => seat.HoldId == holdId)
            .OrderBy(seat => seat.SeatId)
            .Select(seat => new SeatOfHold(seat.SeatId, seat.PriceCategoryId))
            .ToListAsync(cancellationToken);
}

internal sealed class HoldRepository(InventoryDbContext db) : IHoldRepository
{
    public void Add(Hold hold) => db.Holds.Add(hold);

    public Task<Hold?> FindAsync(Guid holdId, CancellationToken cancellationToken) =>
        db.Holds.SingleOrDefaultAsync(hold => hold.Id == holdId, cancellationToken);
}
