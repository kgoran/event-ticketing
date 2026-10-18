using EventTicketing.Catalog.Events;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventTicketing.Catalog.Persistence;

internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "catalog";

    public DbSet<Event> Events => Set<Event>();

    protected override string Schema => SchemaName;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Event>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Currency).HasMaxLength(3);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasMany(e => e.PriceCategories).WithOne().HasForeignKey(c => c.EventId);
            entity.HasMany(e => e.Seats).WithOne().HasForeignKey(s => s.EventId);
            entity.HasIndex(e => e.OrganizerId);
        });

        modelBuilder.Entity<PriceCategory>(entity =>
        {
            entity.ToTable("price_categories");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<Seat>(entity =>
        {
            entity.ToTable("seats");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Section).HasMaxLength(50);
            entity.Property(s => s.Row).HasMaxLength(20);
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>.</summary>
internal sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(ModuleDesignTime.Options<CatalogDbContext>(CatalogDbContext.SchemaName));
}
