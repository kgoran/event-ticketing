using EventTicketing.CheckIn.Application;
using EventTicketing.CheckIn.Infrastructure.Endpoints;
using EventTicketing.CheckIn.Infrastructure.Identity;
using EventTicketing.CheckIn.Infrastructure.Persistence;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.CheckIn.Infrastructure;

/// <summary>
/// CheckIn (ADR-0013, ADR-0056): each event's door list, built from Ticketing's events; the devices,
/// each a Keycloak client; the scans they upload, and duplicates. CheckInApi serves the devices,
/// BackOfficeApi registers and revokes them, and the consumers run in Workers.
/// </summary>
public sealed class CheckInModule : IModule
{
    public string Name => "CheckIn";

    public void Register(IHostApplicationBuilder builder, HostKind host)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModuleDatabase<CheckInDbContext>(host, CheckInDbContext.SchemaName);

        var application = typeof(IDoorList).Assembly;
        builder.Services.AddCommandHandlers(application);
        builder.Services.AddMessageHandlers(application);

        builder.Services.AddScoped<IDoorList, DoorList>();
        builder.Services.AddScoped<IScanLog, ScanLog>();
        builder.Services.AddScoped<IDevices, Devices>();
        builder.Services.AddScoped<ICheckInEvents, CheckInEvents>();

        // Devices get credentials only where an identity provider is configured (BackOfficeApi).
        var keycloak = builder.Configuration.GetSection("CheckIn:Keycloak");
        builder.Services.Configure<CheckInKeycloakOptions>(keycloak);
        builder.Services.AddHttpClient(nameof(DeviceTokens));
        if (!string.IsNullOrWhiteSpace(keycloak["ClientSecret"]))
        {
            builder.Services.AddSingleton<CheckInAdminToken>();
            builder.Services.AddHttpClient<IDeviceDirectory, KeycloakDevices>();
        }
        else
        {
            builder.Services.AddSingleton<IDeviceDirectory, UnconfiguredDevices>();
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
        if (host == HostKind.CheckInApi)
        {
            DoorEndpoints.MapForDevices(endpoints);
            DeviceTokens.Map(endpoints);
        }

        if (host == HostKind.BackOfficeApi)
        {
            DeviceEndpoints.MapForBackOffice(endpoints);
        }
    }
}
