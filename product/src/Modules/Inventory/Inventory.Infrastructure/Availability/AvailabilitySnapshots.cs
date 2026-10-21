using System.Text.Json;
using EventTicketing.Inventory.Infrastructure.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EventTicketing.Inventory.Infrastructure.Availability;

/// <summary>
/// Availability snapshots and sold-out flags in Valkey ([ADR-0011] levers 2 and 3, D53): one
/// snapshot per on-sale event, rebuilt about once a second by one Workers instance, read by every
/// PublicApi instance instead of three queries per request; and a flag per sold-out section, so a
/// hold for it is refused without touching the database. Both may be lost or a second old: they
/// are advice, never a decision (the claim decides). Without Valkey, or when it fails, there is
/// no snapshot and no flag, and everything works as before.
/// </summary>
internal sealed partial class AvailabilitySnapshots(IServiceProvider services, ILogger<AvailabilitySnapshots> logger)
{
    /// <summary>A snapshot older than this is gone: the endpoint asks the database again.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    /// <summary>How long a Workers instance owns the next rebuild of an event.</summary>
    private static readonly TimeSpan RebuildClaim = TimeSpan.FromMilliseconds(900);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IDatabase? Valkey => services.GetService<IConnectionMultiplexer>()?.GetDatabase();

    /// <summary>Whether snapshots are kept at all (the host has Valkey).</summary>
    public bool Enabled => services.GetService<IConnectionMultiplexer>() is not null;

    public async Task<Endpoints.Availability?> ReadAsync(Guid eventId)
    {
        if (Valkey is not { } valkey)
        {
            return null;
        }

        try
        {
            var value = await valkey.StringGetAsync(SnapshotKey(eventId));
            return value.HasValue ? JsonSerializer.Deserialize<Endpoints.Availability>(value.ToString(), Json) : null;
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or JsonException)
        {
            Log.Unavailable(logger, exception);
            return null;
        }
    }

    public async Task<bool> IsSoldOutAsync(Guid eventId, string section)
    {
        if (Valkey is not { } valkey)
        {
            return false;
        }

        try
        {
            return await valkey.KeyExistsAsync(SoldOutKey(eventId, section));
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            Log.Unavailable(logger, exception);
            return false;
        }
    }

    /// <summary>Whether this instance builds the event's next snapshot: one instance a second does.</summary>
    public async Task<bool> ClaimRebuildAsync(Guid eventId)
    {
        if (Valkey is not { } valkey)
        {
            return false;
        }

        try
        {
            return await valkey.StringSetAsync(ClaimKey(eventId), Environment.MachineName, RebuildClaim, When.NotExists);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            Log.Unavailable(logger, exception);
            return false;
        }
    }

    public async Task WriteAsync(Endpoints.Availability availability)
    {
        if (Valkey is not { } valkey)
        {
            return;
        }

        try
        {
            var batch = valkey.CreateBatch();
            var writes = new List<Task> { batch.StringSetAsync(SnapshotKey(availability.EventId), JsonSerializer.Serialize(availability, Json), Lifetime) };
            foreach (var section in availability.Sections)
            {
                writes.Add(section.Available == 0
                    ? batch.StringSetAsync(SoldOutKey(availability.EventId, section.Section), 1, Lifetime)
                    : batch.KeyDeleteAsync(SoldOutKey(availability.EventId, section.Section)));
            }

            batch.Execute();
            await Task.WhenAll(writes);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            Log.Unavailable(logger, exception);
        }
    }

    private static string SnapshotKey(Guid eventId) => $"inventory:availability:{eventId}";

    private static string SoldOutKey(Guid eventId, string section) => $"inventory:sold-out:{eventId}:{section}";

    private static string ClaimKey(Guid eventId) => $"inventory:availability-rebuild:{eventId}";

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Valkey is unavailable: availability comes from the database")]
        public static partial void Unavailable(ILogger logger, Exception exception);
    }
}
