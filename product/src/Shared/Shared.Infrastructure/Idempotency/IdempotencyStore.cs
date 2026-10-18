using System.Text.Json;
using EventTicketing.Shared.Application.Idempotency;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EventTicketing.Shared.Infrastructure.Idempotency;

/// <summary>
/// Idempotency records in the schema of the module that owns the command. Claims and completions
/// are short statements of their own, outside the command's transaction (the pipeline order puts
/// idempotency outside the transaction, ADR-0033). If a process dies after its transaction
/// committed but before completing the record, the claim expires and a retry runs the command
/// again; commands guard against that with their own rules (for example, one order per hold).
/// </summary>
internal sealed class IdempotencyStore(IServiceProvider services, ModuleRegistry modules, IOptions<MessagingOptions> options)
    : IIdempotencyStore
{
    public async Task<IdempotencyClaim> TryClaimAsync(string key, Type commandType, CancellationToken cancellationToken)
    {
        var (module, context, request) = Resolve(commandType);
        var lease = options.Value.IdempotencyLease.TotalSeconds;

        var inserted = await ModuleSql.ExecuteAsync(
            context,
            $"INSERT INTO {module.Schema}.idempotency_records (key, request, status, locked_until, created_at) VALUES ({{0}}, {{1}}, 'in_progress', now() + make_interval(secs => {{2}}), now()) ON CONFLICT (key, request) DO NOTHING",
            [key, request, lease],
            cancellationToken);

        if (inserted == 1)
        {
            return new IdempotencyClaim.Claimed();
        }

        // The earlier request with this key died without completing: take over its expired claim.
        var reclaimed = await ModuleSql.ExecuteAsync(
            context,
            $"UPDATE {module.Schema}.idempotency_records SET locked_until = now() + make_interval(secs => {{2}}) WHERE key = {{0}} AND request = {{1}} AND status = 'in_progress' AND locked_until < now()",
            [key, request, lease],
            cancellationToken);

        if (reclaimed == 1)
        {
            return new IdempotencyClaim.Claimed();
        }

        var stored = await ModuleSql.QueryAsync(
            context,
            $"SELECT status, result::text FROM {module.Schema}.idempotency_records WHERE key = @key AND request = @request",
            reader => (Status: reader.GetString(0), Result: reader.IsDBNull(1) ? null : reader.GetString(1)),
            cancellationToken,
            ("key", key),
            ("request", request));

        return stored is [{ Status: "completed", Result: { } json }]
            ? new IdempotencyClaim.AlreadyCompleted(JsonSerializer.Deserialize<StoredResult>(json, MessagingJson.Options)!)
            : new IdempotencyClaim.InProgress();
    }

    public async Task CompleteAsync(string key, Type commandType, StoredResult result, CancellationToken cancellationToken)
    {
        var (module, context, request) = Resolve(commandType);

        await ModuleSql.ExecuteAsync(
            context,
            $"UPDATE {module.Schema}.idempotency_records SET status = 'completed', result = CAST({{2}} AS jsonb), completed_at = now() WHERE key = {{0}} AND request = {{1}}",
            [key, request, JsonSerializer.Serialize(result, MessagingJson.Options)],
            cancellationToken);
    }

    public async Task ReleaseAsync(string key, Type commandType, CancellationToken cancellationToken)
    {
        var (module, context, request) = Resolve(commandType);

        await ModuleSql.ExecuteAsync(
            context,
            $"DELETE FROM {module.Schema}.idempotency_records WHERE key = {{0}} AND request = {{1}} AND status = 'in_progress'",
            [key, request],
            cancellationToken);
    }

    private (ModuleDatabase Module, ModuleDbContext Context, string Request) Resolve(Type commandType)
    {
        ArgumentNullException.ThrowIfNull(commandType);

        var module = modules.Resolve(commandType);
        return (module, (ModuleDbContext)services.GetRequiredService(module.ContextType), commandType.FullName ?? commandType.Name);
    }
}
