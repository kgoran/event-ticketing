using EventTicketing.Organizers.Identity;
using EventTicketing.Organizers.Persistence;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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

        // Keycloak is kept in step only when its service account is configured; module tests and
        // hosts without it keep membership as a record, as in M1a.
        var keycloak = builder.Configuration.GetSection("Organizers:Keycloak");
        builder.Services.Configure<KeycloakDirectoryOptions>(keycloak);
        if (!string.IsNullOrWhiteSpace(keycloak["ClientSecret"]))
        {
            builder.Services.AddSingleton<KeycloakAdminToken>();
            builder.Services.AddHttpClient<IIdentityDirectory, KeycloakDirectory>();
        }
        else
        {
            builder.Services.AddSingleton<IIdentityDirectory, UnconfiguredDirectory>();
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
        if (host == HostKind.BackOfficeApi)
        {
            OrganizerEndpoints.Map(endpoints);
        }
    }
}
