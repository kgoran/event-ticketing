using EventTicketing.Inventory.Application;
using EventTicketing.Inventory.Application.Holds;
using EventTicketing.Inventory.Contracts;
using EventTicketing.Inventory.Infrastructure.Endpoints;
using EventTicketing.Inventory.Infrastructure.Persistence;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
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

        var application = typeof(PlaceHold).Assembly;
        builder.Services.AddCommandHandlers(application);
        builder.Services.AddMessageHandlers(application);

        builder.Services.AddSingleton(builder.Configuration.GetSection("Inventory").Get<InventoryPolicy>() ?? new InventoryPolicy());
        builder.Services.AddScoped<IHoldRepository, HoldRepository>();
        builder.Services.AddScoped<ISeatInventory, SeatInventory>();
        builder.Services.AddScoped<IInventoryApi, InventoryApi>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
        if (host == HostKind.PublicApi)
        {
            SeatEndpoints.MapForFans(endpoints);
        }
    }
}
