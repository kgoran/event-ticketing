using EventTicketing.Organizers.Persistence;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Organizers;

public sealed class OrganizersModule : IModule
{
    public string Name => "Organizers";

    public void Register(IHostApplicationBuilder builder, HostKind host)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModuleDatabase<OrganizersDbContext>(host, OrganizersDbContext.SchemaName);
        builder.Services.AddCommandHandlers(typeof(OrganizersModule).Assembly);
        builder.Services.AddMessageHandlers(typeof(OrganizersModule).Assembly);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
        if (host == HostKind.BackOfficeApi)
        {
            OrganizerEndpoints.Map(endpoints);
        }
    }
}
