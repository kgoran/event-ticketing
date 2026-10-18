using System.Text.Json;
using EventTicketing.Shared.Application.Transactions;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Context;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketing.Shared.Infrastructure.Messaging;

internal sealed record ClaimedMessage(
    Guid Id,
    string Type,
    int Version,
    string Payload,
    string Headers,
    DateTimeOffset OccurredAt,
    int Attempts);

/// <summary>
/// One pass of the outbox relay over one module (ADR-0014):
/// <list type="number">
/// <item>Claim due messages with <c>FOR UPDATE SKIP LOCKED</c>, so several Workers instances never take the same message at once.</item>
/// <item>Deliver each message to every consumer, each in its own module's transaction with an inbox row.</item>
/// <item>Mark it processed, schedule a retry with backoff, or dead-letter it after the last attempt.</item>
/// </list>
/// A consumer that already handled the message finds its inbox row and is skipped, so retrying a
/// message after one consumer failed doesn't repeat the others. In M1–M3 delivery is in-process;
/// from M4 the relay publishes to Kafka instead.
/// </summary>
internal sealed partial class OutboxRelay(
    IServiceProvider services,
    IServiceScopeFactory scopes,
    ModuleRegistry modules,
    MessageHandlerRegistry handlers,
    TimeProvider timeProvider,
    IOptions<MessagingOptions> options,
    ILogger<OutboxRelay> logger)
{
    public async Task<int> RunOnceAsync(ModuleDatabase module, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(module);

        var context = (ModuleDbContext)services.GetRequiredService(module.ContextType);
        var claimed = await ClaimAsync(context, module, cancellationToken);

        foreach (var message in claimed)
        {
            await DeliverAsync(context, module, message, cancellationToken);
        }

        return claimed.Count;
    }

    private Task<List<ClaimedMessage>> ClaimAsync(ModuleDbContext context, ModuleDatabase module, CancellationToken cancellationToken)
    {
        // The schema is a validated identifier (AddModuleDatabase); every value is a parameter.
        var sql = $"""
            UPDATE {module.Schema}.outbox_messages AS o
            SET locked_until = now() + make_interval(secs => @lease), attempts = o.attempts + 1
            WHERE o.id IN (
                SELECT id FROM {module.Schema}.outbox_messages
                WHERE processed_at IS NULL AND dead_lettered_at IS NULL
                  AND next_attempt_at <= now()
                  AND (locked_until IS NULL OR locked_until < now())
                ORDER BY id
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            RETURNING o.id, o.type, o.version, o.payload::text, o.headers::text, o.occurred_at, o.attempts
            """;

        return ModuleSql.QueryAsync(
            context,
            sql,
            reader => new ClaimedMessage(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetInt32(6)),
            cancellationToken,
            ("lease", options.Value.Lease.TotalSeconds),
            ("batch", options.Value.BatchSize));
    }

    private async Task DeliverAsync(ModuleDbContext context, ModuleDatabase module, ClaimedMessage message, CancellationToken cancellationToken)
    {
        var typeTag = new KeyValuePair<string, object?>("eventticketing.message.type", message.Type);
        Exception? failure = null;

        foreach (var consumer in handlers.ConsumersOf(message.Type, message.Version))
        {
            try
            {
                await DeliverToConsumerAsync(message, consumer, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failure ??= exception;
                LogConsumerFailed(logger, exception, message.Type, message.Id, consumer.ConsumerName, message.Attempts);
            }
        }

        if (failure is null)
        {
            await ModuleSql.ExecuteAsync(
                context,
                $"UPDATE {module.Schema}.outbox_messages SET processed_at = now(), locked_until = NULL WHERE id = {{0}}",
                [message.Id],
                cancellationToken);

            MessagingTelemetry.Delivered.Add(1, typeTag);
            MessagingTelemetry.Lag.Record((timeProvider.GetUtcNow() - message.OccurredAt).TotalSeconds, typeTag);
        }
        else if (message.Attempts >= options.Value.MaxAttempts)
        {
            await ModuleSql.ExecuteAsync(
                context,
                $"UPDATE {module.Schema}.outbox_messages SET dead_lettered_at = now(), locked_until = NULL, last_error = {{1}} WHERE id = {{0}}",
                [message.Id, ModuleSql.ErrorText(failure)],
                cancellationToken);

            MessagingTelemetry.DeadLettered.Add(1, typeTag);
            LogDeadLettered(logger, message.Type, message.Id, message.Attempts);
        }
        else
        {
            await ModuleSql.ExecuteAsync(
                context,
                $"UPDATE {module.Schema}.outbox_messages SET locked_until = NULL, next_attempt_at = now() + make_interval(secs => {{1}}), last_error = {{2}} WHERE id = {{0}}",
                [message.Id, options.Value.Backoff(message.Attempts).TotalSeconds, ModuleSql.ErrorText(failure)],
                cancellationToken);

            MessagingTelemetry.Retried.Add(1, typeTag);
        }
    }

    private async Task DeliverToConsumerAsync(ClaimedMessage message, ConsumerRegistration consumer, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        // Commands the consumer runs are idempotent per message and consumer (ADR-0033).
        provider.GetRequiredService<MessageExecutionContext>().IdempotencyKey = $"{message.Id}:{consumer.ConsumerName}";

        var consumerModule = modules.Resolve(consumer.HandlerType);
        var integrationEvent = JsonSerializer.Deserialize(message.Payload, consumer.EventType, MessagingJson.Options)
            ?? throw new InvalidOperationException($"Message {message.Id} has an empty payload.");

        using var activity = MessagingTelemetry.StartProcessing("consume", message.Type, message.Id, MessageHeaders.FromJson(message.Headers));
        activity?.SetTag("eventticketing.consumer", consumer.ConsumerName);

        await provider.GetRequiredService<ITransactionRunner>().RunAsync<Unit>(
            consumer.HandlerType,
            async token =>
            {
                // The inbox row commits together with the handler's changes, so a redelivered
                // message finds it and is dropped before the handler runs again.
                var consumerContext = (ModuleDbContext)provider.GetRequiredService(consumerModule.ContextType);
                var isFirstDelivery = await ModuleSql.ExecuteAsync(
                    consumerContext,
                    $"INSERT INTO {consumerModule.Schema}.inbox_messages (message_id, consumer, processed_at) VALUES ({{0}}, {{1}}, now()) ON CONFLICT DO NOTHING",
                    [message.Id, consumer.ConsumerName],
                    token) == 1;

                if (!isFirstDelivery)
                {
                    activity?.SetTag("eventticketing.message.duplicate", true);
                    MessagingTelemetry.Duplicates.Add(1, new KeyValuePair<string, object?>("eventticketing.message.type", message.Type));
                    return Unit.Value;
                }

                await consumer.Invoke(provider.GetRequiredService(consumer.HandlerType), integrationEvent, token);
                return Unit.Value;
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} failed on {MessageType} {MessageId} (attempt {Attempt})")]
    private static partial void LogConsumerFailed(ILogger logger, Exception exception, string messageType, Guid messageId, string consumer, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dead-lettered {MessageType} {MessageId} after {Attempts} attempts")]
    private static partial void LogDeadLettered(ILogger logger, string messageType, Guid messageId, int attempts);
}
