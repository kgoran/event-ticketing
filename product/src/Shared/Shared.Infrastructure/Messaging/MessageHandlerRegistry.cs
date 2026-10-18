using System.Collections.Concurrent;
using System.Reflection;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Application.Scheduling;
using EventTicketing.Shared.Contracts;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Shared.Infrastructure.Messaging;

internal sealed record ConsumerRegistration(
    Type EventType,
    Type HandlerType,
    string ConsumerName,
    Func<object, object, CancellationToken, Task> Invoke);

internal sealed record ScheduledHandlerRegistration(
    Type MessageType,
    Type HandlerType,
    Func<object, object, CancellationToken, Task> Invoke);

/// <summary>Which handlers consume which integration events, and which handle which scheduled messages.</summary>
public sealed class MessageHandlerRegistry
{
    private readonly Dictionary<(string Name, int Version), List<ConsumerRegistration>> _consumers = [];
    private readonly Dictionary<string, ScheduledHandlerRegistration> _scheduled = new(StringComparer.Ordinal);

    internal IReadOnlyList<ConsumerRegistration> ConsumersOf(string name, int version) =>
        _consumers.TryGetValue((name, version), out var consumers) ? consumers : [];

    internal ScheduledHandlerRegistration? ScheduledHandlerFor(string messageType) =>
        _scheduled.GetValueOrDefault(messageType);

    internal void AddConsumer(string name, int version, ConsumerRegistration consumer)
    {
        if (!_consumers.TryGetValue((name, version), out var consumers))
        {
            consumers = [];
            _consumers[(name, version)] = consumers;
        }

        if (consumers.All(existing => existing.HandlerType != consumer.HandlerType))
        {
            consumers.Add(consumer);
        }
    }

    internal void AddScheduledHandler(string messageType, ScheduledHandlerRegistration handler)
    {
        if (_scheduled.TryGetValue(messageType, out var existing) && existing.HandlerType != handler.HandlerType)
        {
            throw new InvalidOperationException($"Scheduled message {messageType} already has a handler: {existing.HandlerType.Name}.");
        }

        _scheduled[messageType] = handler;
    }
}

public static class MessageHandlerRegistration
{
    /// <summary>Registers a module's integration event handlers and scheduled message handlers. Call once per module assembly.</summary>
    public static IServiceCollection AddMessageHandlers(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var registry = services.GetOrAddSingletonInstance<MessageHandlerRegistry>();

        foreach (var type in assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }))
        {
            foreach (var contract in type.GetInterfaces().Where(contract => contract.IsGenericType))
            {
                var definition = contract.GetGenericTypeDefinition();
                var argument = contract.GetGenericArguments()[0];

                if (definition == typeof(IIntegrationEventHandler<>))
                {
                    var (name, version) = IntegrationEventMetadata.Of(argument);
                    services.AddScoped(type);
                    registry.AddConsumer(name, version, new ConsumerRegistration(argument, type, type.FullName!, Invokers.ForEventHandler(argument)));
                }
                else if (definition == typeof(IScheduledMessageHandler<>))
                {
                    services.AddScoped(type);
                    registry.AddScheduledHandler(argument.FullName!, new ScheduledHandlerRegistration(argument, type, Invokers.ForScheduledHandler(argument)));
                }
            }
        }

        return services;
    }
}

/// <summary>Stable event names and versions from <see cref="IntegrationEventAttribute"/>, independent of .NET type names.</summary>
internal static class IntegrationEventMetadata
{
    private static readonly ConcurrentDictionary<Type, (string Name, int Version)> Cache = new();

    public static (string Name, int Version) Of(Type eventType) =>
        Cache.GetOrAdd(eventType, type =>
            type.GetCustomAttribute<IntegrationEventAttribute>() is { } attribute
                ? (attribute.Name, attribute.Version)
                : throw new InvalidOperationException(
                    $"Integration event {type.Name} needs an [IntegrationEvent(\"module.event-name\", version)] attribute."));
}

/// <summary>Typed calls into handlers, built once per handler type instead of using reflection per message.</summary>
internal static class Invokers
{
    public static Func<object, object, CancellationToken, Task> ForEventHandler(Type eventType) =>
        Create(nameof(EventHandler), eventType);

    public static Func<object, object, CancellationToken, Task> ForScheduledHandler(Type messageType) =>
        Create(nameof(ScheduledHandler), messageType);

    private static Func<object, object, CancellationToken, Task> Create(string method, Type argument) =>
        (Func<object, object, CancellationToken, Task>)typeof(Invokers)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(argument)
            .Invoke(null, null)!;

    private static Func<object, object, CancellationToken, Task> EventHandler<TEvent>()
        where TEvent : IIntegrationEvent =>
        (handler, message, cancellationToken) =>
            ((IIntegrationEventHandler<TEvent>)handler).HandleAsync((TEvent)message, cancellationToken);

    private static Func<object, object, CancellationToken, Task> ScheduledHandler<TMessage>()
        where TMessage : class =>
        (handler, message, cancellationToken) =>
            ((IScheduledMessageHandler<TMessage>)handler).HandleAsync((TMessage)message, cancellationToken);
}
