using EventTicketing.Orders.Application;
using EventTicketing.Orders.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventTicketing.Orders.Infrastructure.Persistence;

internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "orders";

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<PriceListEntry> PriceList => Set<PriceListEntry>();

    protected override string Schema => SchemaName;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.CustomerId).HasMaxLength(200);
            entity.Property(o => o.Currency).HasMaxLength(3);
            entity.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasMany(o => o.Lines).WithOne().HasForeignKey(line => line.OrderId);
            entity.HasIndex(o => o.HoldId).IsUnique(); // one order per hold
            entity.HasIndex(o => o.CustomerId);

            // Payment events and the deadline can change an order at the same moment: the later
            // save fails instead of overwriting, and its message is retried (ADR-0011).
            entity.Property<uint>("Version").IsRowVersion();
        });

        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.ToTable("order_lines");
            entity.HasKey(line => new { line.OrderId, line.SeatId });
        });

        modelBuilder.Entity<PriceListEntry>(entity =>
        {
            entity.ToTable("price_list");
            entity.HasKey(entry => new { entry.EventId, entry.PriceCategoryId });
            entity.Property(entry => entry.Currency).HasMaxLength(3);
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>.</summary>
internal sealed class OrdersDesignTimeFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args) =>
        new(ModuleDesignTime.Options<OrdersDbContext>(OrdersDbContext.SchemaName));
}

internal sealed class OrderRepository(OrdersDbContext db) : IOrderRepository
{
    public void Add(Order order) => db.Orders.Add(order);

    public Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Orders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);
}

internal sealed class PriceList(OrdersDbContext db) : IPriceList
{
    public async Task ApplyAsync(IReadOnlyCollection<PriceListEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO orders.price_list (event_id, price_category_id, price_minor_units, currency, set_at)
                VALUES ({entry.EventId}, {entry.PriceCategoryId}, {entry.PriceMinorUnits}, {entry.Currency}, {entry.SetAt})
                ON CONFLICT (event_id, price_category_id) DO UPDATE
                SET price_minor_units = excluded.price_minor_units, currency = excluded.currency, set_at = excluded.set_at
                WHERE orders.price_list.set_at < excluded.set_at
                """,
                cancellationToken);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, PriceListEntry>> ForEventAsync(Guid eventId, CancellationToken cancellationToken) =>
        await db.PriceList.AsNoTracking()
            .Where(entry => entry.EventId == eventId)
            .ToDictionaryAsync(entry => entry.PriceCategoryId, cancellationToken);
}
