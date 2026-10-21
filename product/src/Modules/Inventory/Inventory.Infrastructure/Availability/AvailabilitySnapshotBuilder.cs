using EventTicketing.Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventTicketing.Inventory.Infrastructure.Availability;

/// <summary>
/// Rebuilds every on-sale event's availability snapshot about once a second (D53), in the Workers
/// host: whichever instance claims an event's rebuild in Valkey runs its query, on the primary, so
/// one query a second serves every fan watching the event, however many there are.
/// </summary>
internal sealed partial class AvailabilitySnapshotBuilder(
    IServiceScopeFactory scopes,
    AvailabilitySnapshots snapshots,
    TimeProvider time,
    ILogger<AvailabilitySnapshotBuilder> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!snapshots.Enabled)
        {
            return; // no Valkey: the endpoint queries the database, as before M3
        }

        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
                foreach (var eventId in await AvailabilityQuery.OnSaleAsync(db, stoppingToken))
                {
                    if (await snapshots.ClaimRebuildAsync(eventId))
                    {
                        await snapshots.WriteAsync(await AvailabilityQuery.RunAsync(db, eventId, stoppingToken));
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The database or a failover: the next tick tries again, and readers fall back meanwhile.
                Log.RebuildFailed(logger, exception);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Availability snapshots weren't rebuilt this time")]
        public static partial void RebuildFailed(ILogger logger, Exception exception);
    }
}
