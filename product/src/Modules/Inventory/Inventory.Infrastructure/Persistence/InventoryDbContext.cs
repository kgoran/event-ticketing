using EventTicketing.Inventory.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventTicketing.Inventory.Infrastructure.Persistence;

/// <summary>One row per sellable seat per event. Changed only by the conditional SQL in SeatInventory.</summary>
internal sealed class SeatUnit
{
    public Guid EventId { get; init; }

    public Guid SeatId { get; init; }

    public required string Section { get; init; }

    public required string Row { get; init; }

    public int Number { get; init; }

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

    public DbSet<Hold> Holds => Set<Hold>();

    public DbSet<SeatUnit> Seats => Set<SeatUnit>();

    protected override string Schema => SchemaName;

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
        });

        modelBuilder.Entity<Hold>(entity =>
        {
            entity.ToTable("holds");
            entity.HasKey(h => h.Id);
            entity.Property(h => h.CustomerId).HasMaxLength(200);
            entity.Property(h => h.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(h => new { h.EventId, h.CustomerId });
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>.</summary>
internal sealed class InventoryDesignTimeFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) =>
        new(ModuleDesignTime.Options<InventoryDbContext>(InventoryDbContext.SchemaName));
}
