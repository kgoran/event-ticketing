using EventTicketing.Inventory.Domain;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventTicketing.Inventory.Infrastructure.Persistence;

/// <summary>
/// One row per sellable place per event: a seat, or an anonymous slot in a standing area (ADR-0011).
/// Changed only by the conditional SQL in SeatInventory.
/// </summary>
internal sealed class SeatUnit
{
    public Guid EventId { get; init; }

    public Guid SeatId { get; init; }

    public required string Section { get; init; }

    /// <summary>Null for a standing slot.</summary>
    public string? Row { get; init; }

    /// <summary>Null for a standing slot.</summary>
    public int? Number { get; init; }

    /// <summary>Order for "best available" within the section: 1 is best.</summary>
    public int Rank { get; init; }

    public Guid PriceCategoryId { get; init; }

    public required string Status { get; init; }

    public Guid? HoldId { get; init; }

    public DateTimeOffset? HeldUntil { get; init; }

    public Guid? OrderId { get; init; }
}

internal static class SeatStatus
{
    public const string Available = "Available";
    public const string Held = "Held";
    public const string Sold = "Sold";
}

internal sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "inventory";

    /// <summary>At most one Active or InCheckout hold per fan per event (ADR-0011, ADR-0024).</summary>
    public const string OneCurrentHoldPerFan = "ux_holds_one_current_per_fan";

    private static readonly IReadOnlyDictionary<string, Error> Constraints = new Dictionary<string, Error>
    {
        [OneCurrentHoldPerFan] = Error.Conflict("hold.active_hold_exists", "You already hold seats for this event. Release them or check out first."),
    };

    public DbSet<Hold> Holds => Set<Hold>();

    public DbSet<SeatUnit> Seats => Set<SeatUnit>();

    public DbSet<SalesWindow> SalesWindows => Set<SalesWindow>();

    protected override string Schema => SchemaName;

    protected override IReadOnlyDictionary<string, Error> ConstraintErrors => Constraints;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SeatUnit>(entity =>
        {
            entity.ToTable("seats");
            entity.HasKey(s => new { s.EventId, s.SeatId });
            entity.Property(s => s.Section).HasMaxLength(50);
            entity.Property(s => s.Row).HasMaxLength(20);
            entity.Property(s => s.Status).HasMaxLength(20);
            entity.HasIndex(s => s.HoldId);
            entity.HasIndex(s => new { s.EventId, s.Section, s.Rank });
        });

        modelBuilder.Entity<Hold>(entity =>
        {
            entity.ToTable("holds");
            entity.HasKey(h => h.Id);
            entity.Property(h => h.CustomerId).HasMaxLength(200);
            entity.Property(h => h.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(h => new { h.EventId, h.CustomerId })
                .IsUnique()
                .HasFilter("status IN ('Active', 'InCheckout')")
                .HasDatabaseName(OneCurrentHoldPerFan);
        });

        modelBuilder.Entity<SalesWindow>(entity =>
        {
            entity.ToTable("sales_windows");
            entity.HasKey(w => w.EventId);
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>.</summary>
internal sealed class InventoryDesignTimeFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) =>
        new(ModuleDesignTime.Options<InventoryDbContext>(InventoryDbContext.SchemaName));
}
