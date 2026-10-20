using System.Diagnostics;

namespace EventTicketing.Tooling.LoadGenerator;

/// <summary>What the Scaling Lab asks for: the rate, the mix and the event.</summary>
/// <param name="RequestsPerSecond">0 stops the load.</param>
/// <param name="HoldShare">The share of requests that hold places (and release them at once); the rest browse.</param>
/// <param name="EventId">The event to load; null means the first on-sale event PublicApi lists.</param>
/// <param name="Section">The section every hold goes for (a hot section); null spreads them over all sections.</param>
public sealed record LoadSettings(double RequestsPerSecond = 0, double HoldShare = 0.2, Guid? EventId = null, string? Section = null)
{
    public const double MaxRate = 1000;

    /// <summary>The share of the generator's requests that are traced.</summary>
    public const double TracedShare = 0.05;
}

/// <summary>The current settings; changed by the lab at any time.</summary>
public sealed class LoadControl
{
    private LoadSettings _settings = new();

    public LoadSettings Current => Volatile.Read(ref _settings);

    public void Set(LoadSettings settings) => Volatile.Write(ref _settings, settings);
}

/// <summary>
/// Sends requests on a fixed schedule (an open model, ADR-0017): the n-th request since the rate
/// was set is due at n / rate seconds, whether or not earlier answers came back. Slow answers don't
/// slow the schedule down, which is what lets the lab show overload instead of hiding it (no
/// coordinated omission). Requests in flight are capped; past the cap they are counted as skipped.
/// </summary>
internal sealed partial class Pacer(LoadControl control, LoadStats stats, Traffic traffic, ILogger<Pacer> logger) : BackgroundService
{
    /// <summary>More than this many unanswered requests means the system is far past overload; the generator protects itself.</summary>
    public const int MaxInFlight = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        var clock = Stopwatch.StartNew();
        var schedule = new Schedule(0, TimeSpan.Zero);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var settings = control.Current;
            if (settings.RequestsPerSecond != schedule.Rate)
            {
                schedule = new Schedule(settings.RequestsPerSecond, clock.Elapsed);
            }

            for (var due = schedule.DueBy(clock.Elapsed); due > 0; due--)
            {
                schedule = schedule.Issued();
                stats.Offered();
                if (stats.InFlight >= MaxInFlight)
                {
                    stats.Skipped();
                    continue;
                }

                _ = SendAsync(settings, stoppingToken);
            }
        }
    }

    private async Task SendAsync(LoadSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await traffic.SendOneAsync(settings, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.SendFailed(logger, exception);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "A request could not be sent")]
        public static partial void SendFailed(ILogger logger, Exception exception);
    }
}

/// <summary>Which requests are due: <see cref="Sent"/> since <see cref="Since"/> at <see cref="Rate"/> per second.</summary>
internal readonly record struct Schedule(double Rate, TimeSpan Since, long Sent = 0)
{
    /// <summary>How many requests should have been sent by <paramref name="now"/> but haven't (a burst catches up after a pause).</summary>
    public long DueBy(TimeSpan now) => Rate <= 0 ? 0 : Math.Max(0, (long)Math.Floor((now - Since).TotalSeconds * Rate) - Sent);

    public Schedule Issued() => this with { Sent = Sent + 1 };
}
