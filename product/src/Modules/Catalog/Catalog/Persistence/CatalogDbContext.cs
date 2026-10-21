using EventTicketing.Catalog.Events;
using EventTicketing.Catalog.Venues;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NpgsqlTypes;

namespace EventTicketing.Catalog.Persistence;

internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "catalog";

    /// <summary>
    /// The text-search configuration: "simple" lower-cases words without stemming, because event and
    /// venue names mix languages and proper nouns that stemming would mangle.
    /// </summary>
    public const string SearchConfiguration = "simple";

    /// <summary>A stored, generated tsvector over the event and venue names; a shadow property, so the entity stays plain.</summary>
    public const string SearchVectorProperty = "SearchVector";

    public DbSet<Venue> Venues => Set<Venue>();

    public DbSet<Event> Events => Set<Event>();

    protected override string Schema => SchemaName;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Venue>(entity =>
        {
            entity.ToTable("venues");
            entity.HasKey(v => v.Id);
            entity.Property(v => v.Name).HasMaxLength(200);
            entity.Property(v => v.TimeZone).HasMaxLength(64);
            entity.HasMany(v => v.Sections).WithOne().HasForeignKey(s => s.VenueId);
            entity.HasIndex(v => v.OrganizerId);
        });

        modelBuilder.Entity<VenueSection>(entity =>
        {
            entity.ToTable("venue_sections");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Code).HasMaxLength(20);
            entity.Property(s => s.Name).HasMaxLength(100);
            entity.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20);
            entity.HasMany(s => s.Seats).WithOne().HasForeignKey(seat => seat.SectionId);
            entity.HasIndex(s => new { s.VenueId, s.Code }).IsUnique();
            entity.Ignore(s => s.PlaceCount);
        });

        modelBuilder.Entity<VenueSeat>(entity =>
        {
            entity.ToTable("venue_seats");
            entity.HasKey(seat => seat.Id);
            entity.Property(seat => seat.Row).HasMaxLength(20);
            entity.HasIndex(seat => new { seat.SectionId, seat.Row, seat.Number }).IsUnique();
        });

        modelBuilder.Entity<Event>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.VenueName).HasMaxLength(200);
            // The venue's time zone, copied when the event is created: named for that since DP3's worked
            // example of expand and contract (docs/database-changes.md).
            entity.Property(e => e.TimeZone).HasColumnName("venue_time_zone").HasMaxLength(64);
            entity.Property(e => e.Currency).HasMaxLength(3);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.SeatMapVersion).HasMaxLength(12);
            entity.Property<NpgsqlTsVector>(SearchVectorProperty)
                .HasComputedColumnSql($"to_tsvector('{SearchConfiguration}', name || ' ' || venue_name)", stored: true);
            entity.HasIndex(SearchVectorProperty).HasMethod("gin");
            entity.HasIndex(e => new { e.Status, e.StartsAt });
            entity.HasOne<Venue>().WithMany().HasForeignKey(e => e.VenueId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(e => e.PriceCategories).WithOne().HasForeignKey(c => c.EventId);
            entity.HasMany(e => e.PriceAssignments).WithOne().HasForeignKey(a => a.EventId);
            entity.HasIndex(e => e.OrganizerId);
        });

        modelBuilder.Entity<PriceCategory>(entity =>
        {
            entity.ToTable("price_categories");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<PriceAssignment>(entity =>
        {
            entity.ToTable("price_assignments");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Section).HasMaxLength(20);
            entity.Property(a => a.Row).HasMaxLength(20);
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>.</summary>
internal sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(ModuleDesignTime.Options<CatalogDbContext>(CatalogDbContext.SchemaName));
}
