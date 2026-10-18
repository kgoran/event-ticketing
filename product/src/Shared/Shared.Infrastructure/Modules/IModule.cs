using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Shared.Infrastructure.Modules;

/// <summary>The seven deployables (ADR-0008). The same code runs in each; a host picks what it runs.</summary>
public enum HostKind
{
    PublicApi,
    WaitingRoom,
    WebhookIngress,
    BackOfficeApi,
    CheckInApi,
    Workers,
    Migrator,
}

/// <summary>
/// A module's entry point. Each host registers the modules it runs, and each module decides what
/// it contributes to that host: endpoints in the API hosts, consumers in Workers, migrations in
/// the Migrator.
/// </summary>
public interface IModule
{
    string Name { get; }

    void Register(IHostApplicationBuilder builder, HostKind host);

    void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host);
}

/// <summary>Applies one module's database migrations. Run only by the Migrator host (ADR-0031).</summary>
public interface IModuleMigration
{
    string Module { get; }

    Task MigrateAsync(CancellationToken cancellationToken);
}
