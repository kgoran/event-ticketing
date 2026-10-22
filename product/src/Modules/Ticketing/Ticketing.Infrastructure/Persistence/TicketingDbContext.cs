using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Ticketing.Application;
using EventTicketing.Ticketing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventTicketing.Ticketing.Infrastructure.Persistence;

internal sealed class TicketingDbContext(DbContextOptions<TicketingDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "ticketing";

    /// <summary>A ticket per place: a second delivery of the same order can't issue a second one.</summary>
    public const string OneTicketPerPlace = "ux_tickets_order_id_seat_id";

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<TicketedEvent> Events => Set<TicketedEvent>();

    public DbSet<SeatLabel> SeatLabels => Set<SeatLabel>();

    public DbSet<StandingLabel> StandingLabels => Set<StandingLabel>();

    public DbSet<EventKey> EventKeys => Set<EventKey>();

    protected override string Schema => SchemaName;

    protected override IReadOnlyDictionary<string, Error> ConstraintErrors { get; } = new Dictionary<string, Error>
    {
        [OneTicketPerPlace] = Error.Conflict("tickets.already_issued", "This order's tickets are already issued."),
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.ToTable("tickets");
            entity.HasKey(ticket => ticket.Id);
            entity.HasIndex(ticket => new { ticket.OrderId, ticket.SeatId }).IsUnique().HasDatabaseName(OneTicketPerPlace);
            entity.HasIndex(ticket => ticket.CustomerId);
            entity.Property(ticket => ticket.CustomerId).HasMaxLength(200);
            entity.Property(ticket => ticket.Label).HasMaxLength(200);
            entity.Property(ticket => ticket.KeyId).HasMaxLength(64);
            entity.Property(ticket => ticket.Code).HasMaxLength(1000);
            entity.Property(ticket => ticket.RevocationReason).HasMaxLength(200);
        });

        modelBuilder.Entity<TicketedEvent>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.TimeZone).HasMaxLength(64);
        });

        modelBuilder.Entity<SeatLabel>(entity =>
        {
            entity.ToTable("seat_labels");
            entity.HasKey(label => new { label.EventId, label.SeatId });
            entity.Property(label => label.Label).HasMaxLength(200);
        });

        modelBuilder.Entity<StandingLabel>(entity =>
        {
            entity.ToTable("standing_labels");
            entity.HasKey(label => new { label.EventId, label.PriceCategoryId });
            entity.Property(label => label.Label).HasMaxLength(200);
        });

        modelBuilder.Entity<EventKey>(entity =>
        {
            entity.ToTable("event_keys");
            entity.HasKey(key => key.KeyId);
            entity.HasIndex(key => key.EventId);
            entity.Property(key => key.KeyId).HasMaxLength(64);
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>.</summary>
internal sealed class TicketingDesignTimeFactory : IDesignTimeDbContextFactory<TicketingDbContext>
{
    public TicketingDbContext CreateDbContext(string[] args) =>
        new(ModuleDesignTime.Options<TicketingDbContext>(TicketingDbContext.SchemaName));
}

internal sealed class TicketRepository(TicketingDbContext db) : ITicketRepository
{
    public void Add(Ticket ticket) => db.Tickets.Add(ticket);

    public Task<bool> AnyForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Tickets.AnyAsync(ticket => ticket.OrderId == orderId, cancellationToken);

    public Task<Ticket?> FindAsync(Guid ticketId, CancellationToken cancellationToken) =>
        db.Tickets.SingleOrDefaultAsync(ticket => ticket.Id == ticketId, cancellationToken);
}

internal sealed class TicketedEvents(TicketingDbContext db) : ITicketedEvents
{
    public Task<TicketedEvent?> FindAsync(Guid eventId, CancellationToken cancellationToken) =>
        db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId, cancellationToken);

    public async Task AddAsync(TicketedEvent ticketedEvent, IReadOnlyCollection<SeatLabel> seats, IReadOnlyCollection<StandingLabel> standing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticketedEvent);

        if (await db.Events.AnyAsync(e => e.EventId == ticketedEvent.EventId, cancellationToken))
        {
            return;
        }

        db.Events.Add(ticketedEvent);
        db.SeatLabels.AddRange(seats);
        db.StandingLabels.AddRange(standing);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> LabelsAsync(
        Guid eventId,
        IReadOnlyCollection<(Guid SeatId, Guid PriceCategoryId)> places,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(places);

        var seatIds = places.Select(place => place.SeatId).ToList();
        var seats = await db.SeatLabels.AsNoTracking()
            .Where(label => label.EventId == eventId && seatIds.Contains(label.SeatId))
            .ToDictionaryAsync(label => label.SeatId, label => label.Label, cancellationToken);

        var categoryIds = places.Select(place => place.PriceCategoryId).Distinct().ToList();
        var standing = await db.StandingLabels.AsNoTracking()
            .Where(label => label.EventId == eventId && categoryIds.Contains(label.PriceCategoryId))
            .ToDictionaryAsync(label => label.PriceCategoryId, label => label.Label, cancellationToken);

        var labels = new Dictionary<Guid, string>();
        foreach (var (seatId, priceCategoryId) in places)
        {
            if ((seats.GetValueOrDefault(seatId) ?? standing.GetValueOrDefault(priceCategoryId)) is { } label)
            {
                labels[seatId] = label;
            }
        }

        return labels;
    }
}
