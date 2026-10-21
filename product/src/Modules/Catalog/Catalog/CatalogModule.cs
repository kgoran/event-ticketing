using EventTicketing.Catalog.Browse;
using EventTicketing.Catalog.Events;
using EventTicketing.Catalog.Persistence;
using EventTicketing.Catalog.Venues;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Catalog;

public sealed class CatalogModule : IModule
{
    public string Name => "Catalog";

    public void Register(IHostApplicationBuilder builder, HostKind host)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModuleDatabase<CatalogDbContext>(host, CatalogDbContext.SchemaName);
        if (host == HostKind.PublicApi)
        {
            builder.AddModuleReplicaDatabase<CatalogReplicaDbContext>(CatalogDbContext.SchemaName);
        }
        builder.Services.AddCommandHandlers(typeof(CatalogModule).Assembly);
        builder.Services.AddMessageHandlers(typeof(CatalogModule).Assembly);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
        if (host == HostKind.BackOfficeApi)
        {
            VenueEndpoints.MapManagement(endpoints);
            EventEndpoints.MapManagement(endpoints);
        }

        if (host == HostKind.PublicApi)
        {
            BrowseEndpoints.Map(endpoints);
        }
    }
}
