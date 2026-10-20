using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventTicketing.Notifications.Persistence;

internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "notifications";

    /// <summary>One email of a kind per order (ADR-0052).</summary>
    public const string OneEmailPerKindAndOrder = "ux_deliveries_kind_order_id";

    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override string Schema => SchemaName;

    protected override IReadOnlyDictionary<string, Error> ConstraintErrors { get; } = new Dictionary<string, Error>
    {
        [OneEmailPerKindAndOrder] = Error.Conflict("notifications.already_queued", "This email is already on its way."),
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Delivery>(entity =>
        {
            entity.ToTable("deliveries");
            entity.HasKey(delivery => delivery.Id);
            entity.HasIndex(delivery => new { delivery.Kind, delivery.OrderId }).IsUnique().HasDatabaseName(OneEmailPerKindAndOrder);
            entity.Property(delivery => delivery.Kind).HasMaxLength(20);
            entity.Property(delivery => delivery.Recipient).HasMaxLength(320);
            entity.Property(delivery => delivery.Model).HasColumnType("jsonb");
            entity.Property(delivery => delivery.Status).HasConversion<string>().HasMaxLength(20);
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>.</summary>
internal sealed class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(ModuleDesignTime.Options<NotificationsDbContext>(NotificationsDbContext.SchemaName));
}
