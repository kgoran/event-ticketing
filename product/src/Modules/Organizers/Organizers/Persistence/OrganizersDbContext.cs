using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventTicketing.Organizers.Persistence;

internal sealed class OrganizersDbContext(DbContextOptions<OrganizersDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "organizers";

    public DbSet<Organizer> Organizers => Set<Organizer>();

    protected override string Schema => SchemaName;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Organizer>(entity =>
        {
            entity.ToTable("organizers");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Name).HasMaxLength(200);
            entity.HasMany(o => o.Members).WithOne().HasForeignKey(m => m.OrganizerId);
        });

        modelBuilder.Entity<Member>(entity =>
        {
            entity.ToTable("members");
            entity.HasKey(m => new { m.OrganizerId, m.UserId });
            entity.Property(m => m.UserId).HasMaxLength(200);
            entity.Property(m => m.DisplayName).HasMaxLength(200);
            entity.HasIndex(m => m.UserId);
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>.</summary>
internal sealed class OrganizersDesignTimeFactory : IDesignTimeDbContextFactory<OrganizersDbContext>
{
    public OrganizersDbContext CreateDbContext(string[] args) =>
        new(ModuleDesignTime.Options<OrganizersDbContext>(OrganizersDbContext.SchemaName));
}
