using System.Text.Json;
using EventTicketing.Shared.Application.Transactions;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Context;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketing.Shared.Infrastructure.Scheduling;

internal sealed record ClaimedScheduledMessage(Guid Id, string Type, string Payload, string Headers, int Attempts);

/// <summary>
/// One pass of the scheduler over one module: claim messages that are due (SKIP LOCKED), run each
/// handler in the module's transaction, and delete the row in that same transaction. A failed
/// handler rolls back and is retried with backoff, then dead-lettered after the last attempt.
/// All due-time comparisons use the database clock (ADR-0010). Recurring messages get their next
/// occurrence in the same transaction as each run (<see cref="RecurringMessageRegistry"/>).
/// </summary>
internal sealed partial class ScheduledMessageRunner(
    IServiceProvider services,
    IServiceScopeFactory scopes,
    ModuleRegistry modules,
    MessageHandlerRegistry handlers,
    RecurringMessageRegistry recurring,
    TimeProvider time,
    IOptions<MessagingOptions> options,
    ILogger<ScheduledMessageRunner> logger)
{
    public async Task<int> RunOnceAsync(ModuleDatabase module, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(module);

        var context = (ModuleDbContext)services.GetRequiredService(module.ContextType);
        await EnsureRecurringAsync(context, module, cancellationToken);
        var claimed = await ClaimAsync(context, module, cancellationToken);

        foreach (var message in claimed)
        {
            await RunAsync(context, module, message, cancellationToken);
        }

        return claimed.Count;
    }

    /// <summary>
    /// Starts any recurring message of this module that has no occurrence pending: on the first
    /// pass, and again if its occurrence was dead-lettered. Checked about once a minute.
    /// </summary>
    private async Task EnsureRecurringAsync(ModuleDbContext context, ModuleDatabase module, CancellationToken cancellationToken)
    {
        var ours = recurring.MessageTypes.Where(type => modules.Resolve(type) == module).ToList();
        if (ours.Count == 0 || !recurring.IsCheckDue(module.Schema, time.GetUtcNow()))
        {
            return;
        }

        foreach (var type in ours)
        {
            await RecurringMessageSql.EnsurePendingAsync(context, module, type.FullName!, cancellationToken);
        }
    }

    private Task<List<ClaimedScheduledMessage>> ClaimAsync(ModuleDbContext context, ModuleDatabase module, CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE {module.Schema}.scheduled_messages AS s
            SET locked_until = now() + make_interval(secs => @lease), attempts = s.attempts + 1
            WHERE s.id IN (
                SELECT id FROM {module.Schema}.scheduled_messages
                WHERE dead_lettered_at IS NULL
                  AND due_at <= now()
                  AND (locked_until IS NULL OR locked_until < now())
                ORDER BY due_at
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            RETURNING s.id, s.type, s.payload::text, s.headers::text, s.attempts
            """;

        return ModuleSql.QueryAsync(
            context,
            sql,
            reader => new ClaimedScheduledMessage(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4)),
            cancellationToken,
            ("lease", options.Value.Lease.TotalSeconds),
            ("batch", options.Value.BatchSize));
    }

    private async Task RunAsync(ModuleDbContext context, ModuleDatabase module, ClaimedScheduledMessage message, CancellationToken cancellationToken)
    {
        var typeTag = new KeyValuePair<string, object?>("eventticketing.message.type", message.Type);

        try
        {
            var handler = handlers.ScheduledHandlerFor(message.Type)
                ?? throw new InvalidOperationException($"No handler is registered for scheduled message {message.Type}.");

            if (modules.Resolve(handler.HandlerType) != module)
            {
                throw new InvalidOperationException(
                    $"{handler.HandlerType.Name} belongs to a different module than the one that scheduled {message.Type}.");
            }

            await HandleAsync(module, message, handler, cancellationToken);
            MessagingTelemetry.Delivered.Add(1, typeTag);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogHandlerFailed(logger, exception, message.Type, message.Id, message.Attempts);

            if (message.Attempts >= options.Value.MaxAttempts)
            {
                await ModuleSql.ExecuteAsync(
                    context,
                    $"UPDATE {module.Schema}.scheduled_messages SET dead_lettered_at = now(), locked_until = NULL, last_error = {{1}} WHERE id = {{0}}",
                    [message.Id, ModuleSql.ErrorText(exception)],
                    cancellationToken);

                MessagingTelemetry.DeadLettered.Add(1, typeTag);
            }
            else
            {
                await ModuleSql.ExecuteAsync(
                    context,
                    $"UPDATE {module.Schema}.scheduled_messages SET locked_until = NULL, due_at = now() + make_interval(secs => {{1}}), last_error = {{2}} WHERE id = {{0}}",
                    [message.Id, options.Value.Backoff(message.Attempts).TotalSeconds, ModuleSql.ErrorText(exception)],
                    cancellationToken);

                MessagingTelemetry.Retried.Add(1, typeTag);
            }
        }
    }

    private async Task HandleAsync(
        ModuleDatabase module,
        ClaimedScheduledMessage message,
        ScheduledHandlerRegistration handler,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        provider.GetRequiredService<MessageExecutionContext>().IdempotencyKey = message.Id.ToString();

        var payload = JsonSerializer.Deserialize(message.Payload, handler.MessageType, MessagingJson.Options)
            ?? throw new InvalidOperationException($"Scheduled message {message.Id} has an empty payload.");

        using var activity = MessagingTelemetry.StartProcessing("scheduled", message.Type, message.Id, MessageHeaders.FromJson(message.Headers));

        await provider.GetRequiredService<ITransactionRunner>().RunAsync<Unit>(
            handler.HandlerType,
            async token =>
            {
                // Deleting the row in the handler's transaction makes "done" and the handler's work commit together.
                var moduleContext = (ModuleDbContext)provider.GetRequiredService(module.ContextType);
                var deleted = await ModuleSql.ExecuteAsync(
                    moduleContext,
                    $"DELETE FROM {module.Schema}.scheduled_messages WHERE id = {{0}}",
                    [message.Id],
                    token);

                if (deleted == 0)
                {
                    return Unit.Value; // another instance handled it after this one's lease expired
                }

                await handler.Invoke(provider.GetRequiredService(handler.HandlerType), payload, token);

                if (recurring.IntervalOf(message.Type) is { } interval)
                {
                    await RecurringMessageSql.ScheduleNextAsync(moduleContext, module, message.Type, interval, token);
                }

                return Unit.Value;
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handler for scheduled {MessageType} {MessageId} failed (attempt {Attempt})")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string messageType, Guid messageId, int attempt);
}
