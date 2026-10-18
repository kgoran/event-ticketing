using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Shared.Infrastructure.Persistence;

/// <summary>
/// DbContext options for EF Core's design-time tools (<c>dotnet ef migrations add</c>), identical to
/// what AddModuleDatabase configures at runtime, so generated migrations match the running model.
/// The connection string is a placeholder: generating a migration never connects.
/// </summary>
public static class ModuleDesignTime
{
    public static DbContextOptions<TContext> Options<TContext>(string schema)
        where TContext : ModuleDbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql("Host=localhost;Database=design_time", npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", schema))
            .UseSnakeCaseNamingConvention()
            .Options;
}
