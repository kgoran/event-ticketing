using EventTicketing.Inventory.Application;
using EventTicketing.Inventory.Application.Holds;
using EventTicketing.Inventory.Contracts;
using EventTicketing.Inventory.Infrastructure.Availability;
using EventTicketing.Inventory.Infrastructure.Endpoints;
using EventTicketing.Inventory.Infrastructure.Persistence;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Infrastructure.Caching;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Shared.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Inventory.Infrastructure;

public sealed class InventoryModule : IModule
{
    public string Name => "Inventory";

    public void Register(IHostApplicationBuilder builder, HostKind host)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModuleDatabase<InventoryDbContext>(host, InventoryDbContext.SchemaName);
        if (host == HostKind.PublicApi)
        {
            builder.AddModuleReplicaDatabase<InventoryReplicaDbContext>(InventoryDbContext.SchemaName);
        }


        var application = typeof(PlaceHold).Assembly;
        builder.Services.AddCommandHandlers(application);
        builder.Services.AddMessageHandlers(application);

        var policy = builder.Configuration.GetSection("Inventory").Get<InventoryPolicy>() ?? new InventoryPolicy();
        builder.Services.AddSingleton(policy);
        builder.Services.AddRecurringMessage<SweepExpiredHolds>(policy.SweepInterval);
        builder.Services.AddScoped<IHoldRepository, HoldRepository>();
        builder.Services.AddScoped<ISeatInventory, SeatInventory>();
        builder.Services.AddScoped<ISalesWindows, SalesWindows>();
        builder.Services.AddScoped<IInventoryApi, InventoryApi>();

        // Availability snapshots and sold-out flags in Valkey (D53): read by PublicApi, rebuilt by Workers.
        if (host is HostKind.PublicApi or HostKind.Workers)
        {
            builder.AddValkeyClient();
            builder.Services.AddSingleton<AvailabilitySnapshots>();
        }

        if (host == HostKind.Workers)
        {
            builder.Services.AddHostedService<AvailabilitySnapshotBuilder>();
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
        if (host == HostKind.PublicApi)
        {
            SeatEndpoints.MapForFans(endpoints);
        }
    }
}
