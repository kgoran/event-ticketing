using EventTicketing.Shared.Application;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Infrastructure.Context;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Shared.Infrastructure.Modules;

/// <summary>The modules a host runs, and which host it is.</summary>
public sealed class ModuleCatalog(HostKind host, IReadOnlyList<IModule> modules)
{
    public HostKind Host { get; } = host;

    public IReadOnlyList<IModule> Modules { get; } = modules;
}

public static class ModuleRegistration
{
    /// <summary>Registers the given modules for this host, then wraps all command handlers in the pipeline.</summary>
    public static TBuilder AddModules<TBuilder>(this TBuilder builder, HostKind host, params IModule[] modules)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var module in modules)
        {
            module.Register(builder, host);
        }

        if (host is HostKind.Workers or HostKind.Migrator)
        {
            builder.Services.AddScoped<IExecutionContext, SystemExecutionContext>();
        }
        else
        {
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<IExecutionContext, HttpExecutionContext>();
        }

        builder.Services.AddSingleton(new ModuleCatalog(host, modules));
        builder.Services.AddCommandPipeline();

        return builder;
    }

    /// <summary>Maps the endpoints each registered module exposes in this host.</summary>
    public static IEndpointRouteBuilder MapModuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var catalog = endpoints.ServiceProvider.GetRequiredService<ModuleCatalog>();
        foreach (var module in catalog.Modules)
        {
            module.MapEndpoints(endpoints, catalog.Host);
        }

        return endpoints;
    }
}
