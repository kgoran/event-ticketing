using System.Text.RegularExpressions;
using EventTicketing.Shared.Infrastructure.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Shared.Infrastructure.Persistence;

public static partial class ModuleDatabaseRegistration
{
    /// <summary>The connection string all module databases use; each module has its own schema (ADR-0002).</summary>
    public const string ConnectionName = "eventticketing";

    /// <summary>
    /// Registers a module's DbContext: PostgreSQL, snake_case names, the module's schema, and its own
    /// migrations history table. The Migrator host also gets the module's migration (ADR-0031).
    /// </summary>
    /// <param name="moduleNamespace">
    /// The namespace the module's code lives in. Defaults to the first two segments of the
    /// context's namespace, e.g. "EventTicketing.Orders".
    /// </param>
    public static IHostApplicationBuilder AddModuleDatabase<TContext>(
        this IHostApplicationBuilder builder,
        HostKind host,
        string schema,
        string? moduleNamespace = null)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!SchemaName().IsMatch(schema))
        {
            throw new ArgumentException($"'{schema}' is not a valid schema name (lower-case letters, digits and underscores).", nameof(schema));
        }

        moduleNamespace ??= string.Join('.', (typeof(TContext).Namespace ?? string.Empty).Split('.').Take(2));

        // The connection string is read when a context is first created, not here: hosts must be able
        // to start without a database, e.g. when the build generates their OpenAPI documents.
        builder.Services.AddDbContextPool<TContext>((services, options) => options
            .UseNpgsql(
                services.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionName)
                    ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing."),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", schema))
            .UseSnakeCaseNamingConvention());

        // Retries happen at the request level, with idempotency keys, not hidden inside EF Core.
        builder.EnrichNpgsqlDbContext<TContext>(settings => settings.DisableRetry = true);

        builder.Services.GetOrAddSingletonInstance<ModuleRegistry>()
            .Add(new ModuleDatabase(schema, moduleNamespace, typeof(TContext)));

        if (host == HostKind.Migrator)
        {
            builder.Services.AddScoped<IModuleMigration>(services =>
                new ModuleDbMigration(schema, services.GetRequiredService<TContext>()));
        }

        return builder;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SchemaName();
}

/// <summary>Applies one module's EF Core migrations. Run only by the Migrator host.</summary>
internal sealed class ModuleDbMigration(string module, ModuleDbContext context) : IModuleMigration
{
    public string Module { get; } = module;

    public Task MigrateAsync(CancellationToken cancellationToken) => context.Database.MigrateAsync(cancellationToken);
}

internal static class ServiceCollectionExtensions
{
    /// <summary>Returns the singleton instance registered for <typeparamref name="T"/>, registering a new one if there is none.</summary>
    public static T GetOrAddSingletonInstance<T>(this IServiceCollection services)
        where T : class, new()
    {
        var existing = services
            .Select(descriptor => descriptor.ServiceType == typeof(T) ? descriptor.ImplementationInstance as T : null)
            .FirstOrDefault(instance => instance is not null);

        if (existing is not null)
        {
            return existing;
        }

        var instance = new T();
        services.AddSingleton(instance);
        return instance;
    }
}
