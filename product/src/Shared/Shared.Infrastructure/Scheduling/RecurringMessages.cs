using System.Collections.Concurrent;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Shared.Infrastructure.Scheduling;

/// <summary>
/// Scheduled messages that repeat, such as Inventory's sweeper (ADR-0010). Each has exactly one
/// occurrence pending at a time, however many Workers instances run: the scheduler starts it when
/// none is pending, and schedules the next occurrence in the same transaction as each run.
/// </summary>
public sealed class RecurringMessageRegistry
{
    /// <summary>How often a scheduler pass checks that every recurring message still has an occurrence pending.</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, (Type MessageType, TimeSpan Interval)> _messages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextCheck = new(StringComparer.Ordinal);

    internal TimeSpan? IntervalOf(string messageType) =>
        _messages.TryGetValue(messageType, out var message) ? message.Interval : null;

    internal IEnumerable<Type> MessageTypes => _messages.Values.Select(message => message.MessageType);

    internal void Add(Type messageType, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "A recurring message needs a positive interval.");
        }

        _messages[messageType.FullName!] = (messageType, interval);
    }

    /// <summary>True at most once per <see cref="CheckInterval"/> per module, so passes don't check on every poll.</summary>
    internal bool IsCheckDue(string schema, DateTimeOffset now)
    {
        var next = _nextCheck.GetOrAdd(schema, DateTimeOffset.MinValue);
        return now >= next && _nextCheck.TryUpdate(schema, now + CheckInterval, next);
    }
}

public static class RecurringMessageRegistration
{
    /// <summary>
    /// Runs the scheduled handler of <typeparamref name="TMessage"/> every <paramref name="interval"/>.
    /// The message must have no data: each occurrence is the same.
    /// </summary>
    public static IServiceCollection AddRecurringMessage<TMessage>(this IServiceCollection services, TimeSpan interval)
        where TMessage : class, new()
    {
        services.GetOrAddSingletonInstance<RecurringMessageRegistry>().Add(typeof(TMessage), interval);
        return services;
    }
}

/// <summary>The SQL behind <see cref="RecurringMessageRegistry"/>, on the module's connection.</summary>
internal static class RecurringMessageSql
{
    private const string EmptyJson = "{}";

    /// <summary>
    /// Inserts a first occurrence, due now, unless one is pending. The advisory lock makes two
    /// instances checking at the same moment take turns, so only one inserts.
    /// </summary>
    public static async Task EnsurePendingAsync(ModuleDbContext context, ModuleDatabase module, string messageType, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        await ModuleSql.ExecuteAsync(
            context,
            "SELECT pg_advisory_xact_lock(hashtext({0}))",
            [$"recurring:{module.Schema}:{messageType}"],
            cancellationToken);

        await ModuleSql.ExecuteAsync(
            context,
            $$"""
            INSERT INTO {{module.Schema}}.scheduled_messages (id, type, payload, headers, due_at, attempts)
            SELECT {0}, {1}, {2}::jsonb, {2}::jsonb, now(), 0
            WHERE NOT EXISTS (
                SELECT 1 FROM {{module.Schema}}.scheduled_messages WHERE type = {1} AND dead_lettered_at IS NULL)
            """,
            [Guid.CreateVersion7(), messageType, EmptyJson],
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Schedules the next occurrence, in the transaction of the run that just happened (database clock).</summary>
    public static Task ScheduleNextAsync(ModuleDbContext context, ModuleDatabase module, string messageType, TimeSpan interval, CancellationToken cancellationToken) =>
        ModuleSql.ExecuteAsync(
            context,
            $$"""
            INSERT INTO {{module.Schema}}.scheduled_messages (id, type, payload, headers, due_at, attempts)
            VALUES ({0}, {1}, {2}::jsonb, {2}::jsonb, now() + make_interval(secs => {3}), 0)
            """,
            [Guid.CreateVersion7(), messageType, EmptyJson, interval.TotalSeconds],
            cancellationToken);
}
