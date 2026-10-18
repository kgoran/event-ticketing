using System.Reflection;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Pipeline;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventTicketing.Shared.Application;

public static class DependencyInjection
{
    /// <summary>Registers a module's command handlers, authorizers and validators. Call once per module assembly.</summary>
    public static IServiceCollection AddCommandHandlers(this IServiceCollection services, Assembly assembly)
    {
        services.Scan(scan => scan
            .FromAssemblies(assembly)
            .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false)
            .AsImplementedInterfaces()
            .WithScopedLifetime()
            .AddClasses(classes => classes.AssignableTo(typeof(ICommandAuthorizer<>)), publicOnly: false)
            .AsImplementedInterfaces()
            .WithScopedLifetime());

        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped, includeInternalTypes: true);

        return services;
    }

    /// <summary>
    /// Wraps every registered command handler in the pipeline (ADR-0033), outermost first:
    /// context → authorization → validation → idempotency → transaction + outbox → audit → tracing.
    /// Call once, after all modules have registered their handlers.
    /// </summary>
    public static IServiceCollection AddCommandPipeline(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        // Each decoration wraps the previous one, so register from the innermost step outward.
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(TracingDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(AuditDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(TransactionDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(IdempotencyDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidationDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(AuthorizationDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ContextDecorator<,>));

        return services;
    }
}
