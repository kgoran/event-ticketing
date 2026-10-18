using EventTicketing.Inventory.Application;
using EventTicketing.Inventory.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Inventory.Infrastructure.Persistence;

/// <summary>
/// The seat statements from ADR-0011. Each runs inside the command's transaction, takes row locks for
/// one statement only, and never waits on another claim (<c>SKIP LOCKED</c>): a place someone else is
/// claiming at that moment simply counts as unavailable. Every expiry check uses the database clock.
/// </summary>
internal sealed class SeatInventory(InventoryDbContext db) : ISeatInventory
{
    /// <summary>
    /// A place is claimable if it's free or its hold has run out, and only while the event is on
    /// sale. Expiry is checked here, inside the claim: nothing has to sweep expired holds first.
    /// </summary>
    private const string Claimable = """
        (status = 'Available' OR (status = 'Held' AND held_until < now()))
        AND EXISTS (
            SELECT 1 FROM inventory.sales_windows AS w
            WHERE w.event_id = @event AND w.on_sale_at <= now() AND now() < w.sales_end_at)
        """;

    private const string ClaimAndReturn = """
        UPDATE inventory.seats AS s
        SET status = 'Held', hold_id = @hold, held_until = now() + @lifetime, order_id = NULL
        FROM claimable
        WHERE s.event_id = @event AND s.seat_id = claimable.seat_id
        RETURNING s.seat_id, s.held_until
        """;

    public Task AddPlacesAsync(Guid eventId, IReadOnlyList<NewPlace> places, CancellationToken cancellationToken)
    {
        db.Seats.AddRange(places.Select(place => new SeatUnit
        {
            EventId = eventId,
            SeatId = place.SeatId,
            Section = place.Section,
            Row = place.Row,
            Number = place.Number,
            Rank = place.Rank,
            PriceCategoryId = place.PriceCategoryId,
            Status = SeatStatus.Available,
        }));

        return Task.CompletedTask;
    }

    public async Task<PlaceClaim?> TryClaimAsync(
        Guid eventId,
        IReadOnlyCollection<Guid> seatIds,
        Guid holdId,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        // Locks are taken in seat order, so two claims can never deadlock.
        var claimed = await ClaimAsync(
            $"""
            WITH claimable AS (
                SELECT seat_id FROM inventory.seats
                WHERE event_id = @event AND seat_id = ANY(@seats) AND {Claimable}
                ORDER BY seat_id
                FOR UPDATE SKIP LOCKED)
            {ClaimAndReturn}
            """,
            cancellationToken,
            ("event", eventId),
            ("seats", seatIds.ToArray()),
            ("hold", holdId),
            ("lifetime", lifetime));

        return claimed.Count == seatIds.Count ? Claim(claimed) : null;
    }

    public async Task<PlaceClaim?> TryClaimBestAsync(
        Guid eventId,
        string section,
        int quantity,
        Guid holdId,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(
            $"""
            WITH claimable AS (
                SELECT seat_id FROM inventory.seats
                WHERE event_id = @event AND section = @section AND {Claimable}
                ORDER BY rank
                LIMIT @quantity
                FOR UPDATE SKIP LOCKED)
            {ClaimAndReturn}
            """,
            cancellationToken,
            ("event", eventId),
            ("section", section),
            ("quantity", quantity),
            ("hold", holdId),
            ("lifetime", lifetime));

        return claimed.Count == quantity ? Claim(claimed) : null;
    }

    public Task<bool> IsOnSaleAsync(Guid eventId, CancellationToken cancellationToken) =>
        db.SalesWindows.AnyAsync(
            w => w.EventId == eventId && w.OnSaleAt <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow < w.SalesEndAt,
            cancellationToken);

    public async Task ReleaseAsync(Guid holdId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE inventory.seats
            SET status = 'Available', hold_id = NULL, held_until = NULL
            WHERE hold_id = {holdId} AND status = 'Held'
            """,
            cancellationToken);

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

    private static PlaceClaim Claim(List<(Guid SeatId, DateTimeOffset HeldUntil)> claimed) =>
        new([.. claimed.Select(c => c.SeatId).Order()], claimed[0].HeldUntil);

    private Task<List<(Guid SeatId, DateTimeOffset HeldUntil)>> ClaimAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters) =>
        ModuleSql.QueryAsync(db, sql, reader => (reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1)), cancellationToken, parameters);
}

internal sealed class HoldRepository(InventoryDbContext db) : IHoldRepository
{
    public void Add(Hold hold) => db.Holds.Add(hold);

    public Task<Hold?> FindAsync(Guid holdId, CancellationToken cancellationToken) =>
        db.Holds.SingleOrDefaultAsync(hold => hold.Id == holdId, cancellationToken);

    public async Task<Hold?> FindCurrentAsync(Guid eventId, string customerId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE inventory.holds SET status = 'Expired'
            WHERE event_id = {eventId} AND customer_id = {customerId} AND status = 'Active' AND expires_at < now()
            """,
            cancellationToken);

        return await db.Holds.SingleOrDefaultAsync(
            hold => hold.EventId == eventId && hold.CustomerId == customerId
                && (hold.Status == HoldStatus.Active || hold.Status == HoldStatus.InCheckout),
            cancellationToken);
    }
}

internal sealed class SalesWindows(InventoryDbContext db) : ISalesWindows
{
    public void Add(SalesWindow window) => db.SalesWindows.Add(window);

    public Task<SalesWindow?> FindAsync(Guid eventId, CancellationToken cancellationToken) =>
        db.SalesWindows.SingleOrDefaultAsync(window => window.EventId == eventId, cancellationToken);
}
