using EventTicketing.Shared.Application;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Ticketing.Application;
using EventTicketing.Ticketing.Contracts;
using EventTicketing.Ticketing.Infrastructure.Endpoints;
using EventTicketing.Ticketing.Infrastructure.Persistence;
using EventTicketing.Ticketing.Infrastructure.Signing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Ticketing.Infrastructure;

/// <summary>
/// Ticketing (ADR-0006, ADR-0051): issues one ticket per place of a confirmed order, with a code
/// signed by the event's key. Its consumers run in Workers; PublicApi serves the tickets and the
/// public keys; BackOfficeApi lets an admin revoke a ticket (D63); CheckIn reads the keys through
/// its module API.
/// </summary>
public sealed class TicketingModule : IModule
{
    public string Name => "Ticketing";

    public void Register(IHostApplicationBuilder builder, HostKind host)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModuleDatabase<TicketingDbContext>(host, TicketingDbContext.SchemaName);

        var application = typeof(ITicketRepository).Assembly;
        builder.Services.AddCommandHandlers(application);
        builder.Services.AddMessageHandlers(application);

        builder.Services.Configure<TicketingOptions>(builder.Configuration.GetSection("Ticketing"));
        builder.Services.AddSingleton<MasterKey>();
        builder.Services.AddSingleton<ITicketSigner, JwsTicketSigner>();
        builder.Services.AddScoped<IEventKeys, EventKeys>();
        builder.Services.AddScoped<ITicketRepository, TicketRepository>();
        builder.Services.AddScoped<ITicketedEvents, TicketedEvents>();
        builder.Services.AddScoped<ITicketingApi, TicketingApi>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
        if (host == HostKind.PublicApi)
        {
            TicketEndpoints.MapForFans(endpoints);
        }

        if (host == HostKind.BackOfficeApi)
        {
            TicketEndpoints.MapForAdmins(endpoints);
        }
    }
}
