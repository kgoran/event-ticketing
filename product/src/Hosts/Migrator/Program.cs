using EventTicketing.Shared.Infrastructure.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

// Applies every module's migrations, then exits: 0 on success, 1 on failure.
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("eventticketing");
builder.AddModules(HostKind.Migrator); // modules are added here as they are built

using var host = builder.Build();
await host.StartAsync();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EventTicketing.Migrator");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var exitCode = 0;

try
{
    var dataSource = host.Services.GetRequiredService<NpgsqlDataSource>();
    await using (var connection = await dataSource.OpenConnectionAsync(timeout.Token))
    {
        logger.LogInformation("Connected to PostgreSQL {Version}", connection.PostgreSqlVersion);
    }

    var migrations = host.Services.GetServices<IModuleMigration>().ToList();
    foreach (var migration in migrations)
    {
        logger.LogInformation("Migrating module {Module}", migration.Module);
        await migration.MigrateAsync(timeout.Token);
    }

    logger.LogInformation("Migrations complete for {Count} modules", migrations.Count);
}
catch (Exception exception)
{
    logger.LogCritical(exception, "Migration failed");
    exitCode = 1;
}

await host.StopAsync();
return exitCode;
