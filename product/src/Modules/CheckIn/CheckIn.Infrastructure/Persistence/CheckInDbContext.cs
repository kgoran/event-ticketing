using EventTicketing.CheckIn.Application;
using EventTicketing.CheckIn.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventTicketing.CheckIn.Infrastructure.Persistence;

internal sealed class CheckInDbContext(DbContextOptions<CheckInDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "checkin";

    /// <summary>A ticket is issued, revoked and admitted at most once each: the first admission counts.</summary>
    public const string OneChangePerTicket = "ux_door_list_ticket_id_change";

    public DbSet<DoorListEntry> DoorList => Set<DoorListEntry>();

    public DbSet<Scan> Scans => Set<Scan>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<CheckInEvent> Events => Set<CheckInEvent>();

    protected override string Schema => SchemaName;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DoorListEntry>(entity =>
        {
            entity.ToTable("door_list");
            entity.HasKey(entry => entry.Sequence);
            entity.Property(entry => entry.Sequence).UseIdentityAlwaysColumn();
            entity.HasIndex(entry => new { entry.EventId, entry.Sequence });
            entity.HasIndex(entry => new { entry.TicketId, entry.Change }).IsUnique().HasDatabaseName(OneChangePerTicket);
        });

        modelBuilder.Entity<Scan>(entity =>
        {
            entity.ToTable("scans");
            entity.HasKey(scan => scan.Id);
            entity.HasIndex(scan => new { scan.EventId, scan.TicketId });
            entity.HasIndex(scan => scan.DeviceId);
            entity.Property(scan => scan.Reason).HasMaxLength(100);
            entity.Property(scan => scan.Outcome).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("devices");
            entity.HasKey(device => device.Id);
            entity.HasIndex(device => device.EventId);
            entity.HasIndex(device => device.ClientId).IsUnique();
            entity.Property(device => device.Name).HasMaxLength(100);
            entity.Property(device => device.ClientId).HasMaxLength(64);
            entity.Property(device => device.RegisteredBy).HasMaxLength(200);
            entity.Ignore(device => device.IsActive);
        });

        modelBuilder.Entity<CheckInEvent>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.Name).HasMaxLength(200);
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>.</summary>
internal sealed class CheckInDesignTimeFactory : IDesignTimeDbContextFactory<CheckInDbContext>
{
    public CheckInDbContext CreateDbContext(string[] args) =>
        new(ModuleDesignTime.Options<CheckInDbContext>(CheckInDbContext.SchemaName));
}

/// <summary>
/// The door list in PostgreSQL. Every write first takes the event's advisory lock for the rest of
/// the transaction (D71). Sequence numbers are handed out when a row is inserted, not when its
/// transaction commits; without the lock two transactions could commit out of sequence, and a
/// device that had already read the later one would never see the earlier, a revocation say.
/// </summary>
internal sealed class DoorList(CheckInDbContext db) : IDoorList
{
    // Advisory locks are one key space for the whole database: the first key says whose they are.
    private const int DoorListLocks = 0x0C4E_C410;

    private readonly HashSet<Guid> _locked = [];

    public async Task LockAsync(Guid eventId, CancellationToken cancellationToken)
    {
        if (_locked.Add(eventId))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({DoorListLocks}, {LockKey(eventId)})", cancellationToken);
        }
    }

    public async Task AddAsync(Guid eventId, IEnumerable<Guid> ticketIds, DoorListChange change, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await LockAsync(eventId, cancellationToken);
        var tickets = ticketIds.ToArray();
        var utc = at.ToUniversalTime();

        // A change the list has already had changes nothing: a message delivered again is harmless.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO checkin.door_list (event_id, ticket_id, change, at)
            SELECT {eventId}, ticket, {(int)change}, {utc} FROM unnest({tickets}) AS ticket
            ON CONFLICT (ticket_id, change) DO NOTHING
            """, cancellationToken);
    }

    public async Task<bool> TryAdmitAsync(Guid eventId, Guid ticketId, Guid deviceId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await LockAsync(eventId, cancellationToken);
        var utc = at.ToUniversalTime();
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO checkin.door_list (event_id, ticket_id, change, at, device_id)
            VALUES ({eventId}, {ticketId}, {(int)DoorListChange.Admitted}, {utc}, {deviceId})
            ON CONFLICT (ticket_id, change) DO NOTHING
            """, cancellationToken);
        return inserted == 1;
    }

    public async Task<(Guid DeviceId, DateTimeOffset At)?> FirstAdmissionAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var first = await db.DoorList.AsNoTracking()
            .Where(entry => entry.TicketId == ticketId && entry.Change == DoorListChange.Admitted)
            .Select(entry => new { entry.DeviceId, entry.At })
            .SingleOrDefaultAsync(cancellationToken);
        return first is { DeviceId: { } deviceId } ? (deviceId, first.At) : null;
    }

    /// <summary>The event's lock: collisions only make two events wait for each other now and then.</summary>
    private static int LockKey(Guid eventId)
    {
        Span<int> parts = stackalloc int[4];
        eventId.TryWriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(parts));
        return parts[0] ^ parts[1] ^ parts[2] ^ parts[3];
    }
}

internal sealed class ScanLog(CheckInDbContext db) : IScanLog
{
    public Task<Scan?> FindAsync(Guid scanId, CancellationToken cancellationToken) =>
        db.Scans.AsNoTracking().SingleOrDefaultAsync(scan => scan.Id == scanId, cancellationToken);

    public void Add(Scan scan) => db.Scans.Add(scan);
}

internal sealed class Devices(CheckInDbContext db) : IDevices
{
    public void Add(Device device) => db.Devices.Add(device);

    public Task<Device?> FindAsync(Guid deviceId, CancellationToken cancellationToken) =>
        db.Devices.SingleOrDefaultAsync(device => device.Id == deviceId, cancellationToken);
}

internal sealed class CheckInEvents(CheckInDbContext db) : ICheckInEvents
{
    public Task<CheckInEvent?> FindAsync(Guid eventId, CancellationToken cancellationToken) =>
        db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId, cancellationToken);

    public async Task AddAsync(CheckInEvent checkInEvent, CancellationToken cancellationToken)
    {
        if (!await db.Events.AnyAsync(e => e.EventId == checkInEvent.EventId, cancellationToken))
        {
            db.Events.Add(checkInEvent);
        }
    }
}
