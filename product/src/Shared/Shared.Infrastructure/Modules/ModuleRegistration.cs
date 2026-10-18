using EventTicketing.Shared.Application;
using EventTicketing.Shared.Application.Auditing;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Application.Idempotency;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Application.Scheduling;
using EventTicketing.Shared.Application.Transactions;
using EventTicketing.Shared.Infrastructure.Auditing;
using EventTicketing.Shared.Infrastructure.Context;
using EventTicketing.Shared.Infrastructure.Idempotency;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Shared.Infrastructure.Scheduling;
using EventTicketing.Shared.Infrastructure.Transactions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <summary>
    /// Registers the given modules for this host, the infrastructure they share (transactions,
    /// outbox, scheduler, idempotency, audit), and wraps every command handler in the pipeline.
    /// In the Workers host it also starts the outbox relay and the scheduler.
    /// </summary>
    public static TBuilder AddModules<TBuilder>(this TBuilder builder, HostKind host, params IModule[] modules)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        services.GetOrAddSingletonInstance<ModuleRegistry>();
        services.GetOrAddSingletonInstance<MessageHandlerRegistry>();
        services.GetOrAddSingletonInstance<RecurringMessageRegistry>();

        foreach (var module in modules)
        {
            module.Register(builder, host);
        }

        services.AddOptions<MessagingOptions>().BindConfiguration("Messaging");
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<ModuleScope>();
        services.AddScoped<ITransactionRunner, TransactionRunner>();
        services.AddScoped<IIntegrationEventPublisher, OutboxPublisher>();
        services.AddScoped<IMessageScheduler, MessageScheduler>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<IAuditLog, AuditLog>();

        if (host is HostKind.Workers or HostKind.Migrator)
        {
            services.AddScoped<MessageExecutionContext>();
            services.AddScoped<IExecutionContext>(provider => provider.GetRequiredService<MessageExecutionContext>());
        }
        else
        {
            services.AddHttpContextAccessor();
            services.AddScoped<IExecutionContext, HttpExecutionContext>();
        }

        if (host == HostKind.Workers)
        {
            services.AddScoped<OutboxRelay>();
            services.AddScoped<ScheduledMessageRunner>();
            services.AddHostedService<OutboxRelayService>();
            services.AddHostedService<SchedulerService>();
        }

        services.AddSingleton(new ModuleCatalog(host, modules));
        services.AddCommandPipeline();

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
