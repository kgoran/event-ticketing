using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// What a host does to itself before it takes traffic (D43): requests to its own endpoints, sent
/// over HTTP once it listens, so it pays its first-time costs (compiling code, building EF Core's
/// queries, filling caches) while it isn't ready. Kubernetes sends it no traffic until then, and
/// its autoscaler ignores the CPU of an instance that isn't ready.
/// </summary>
public delegate Task WarmUpRoutine(HttpClient self, CancellationToken cancellationToken);

/// <summary>Whether the warm-up has finished, or given up: either way, the host may take traffic.</summary>
internal sealed class WarmUpState
{
    private volatile bool _done;

    public bool Done => _done;

    public void Finish() => _done = true;
}

/// <summary>Not ready while warming up; part of /health (readiness), not of /alive (liveness).</summary>
internal sealed class WarmUpHealthCheck(WarmUpState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Done ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Warming up."));
}

/// <summary>
/// The warm-up's requests aren't traffic: they are left out of the HTTP metrics, which the
/// autoscaler reads (AS2), as they are of traces. Counted, a new instance's warm-up would look like
/// load and buy yet another instance.
/// </summary>
internal sealed class WarmUpMetricsFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, request) =>
        {
            if (context.Request.Headers.ContainsKey(WarmUpService.Header) && context.Features.Get<IHttpMetricsTagsFeature>() is { } metrics)
            {
                metrics.MetricsDisabled = true;
            }

            return request(context);
        });
        next(app);
    };
}

internal sealed partial class WarmUpService(
    WarmUpRoutine routine,
    WarmUpState state,
    IServer server,
    IHostApplicationLifetime lifetime,
    ILogger<WarmUpService> logger) : BackgroundService
{
    /// <summary>Marks the warm-up's own requests, so traces and metrics leave them out.</summary>
    public const string Header = "X-Warm-Up";

    /// <summary>A warm-up that takes longer has met a problem: the host takes traffic anyway, and its other checks decide.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The server listens only once every hosted service has started.
            var started = new TaskCompletionSource();
            using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
            {
                await started.Task.WaitAsync(stoppingToken);
            }

            // An in-memory test server has no address, and nothing to warm up.
            if (server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() is not { } address)
            {
                return;
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            limit.CancelAfter(Limit);
            using var self = new HttpClient { BaseAddress = new Uri(Local(address).TrimEnd('/') + "/") };
            self.DefaultRequestHeaders.Add(Header, "1");

            var start = Stopwatch.GetTimestamp();
            await routine(self, limit.Token);
            Log.WarmedUp(logger, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Log.GaveUp(logger, exception);
        }
        finally
        {
            state.Finish();
        }
    }

    /// <summary>A listening address as a client reaches it: http://[::]:8080 → http://localhost:8080.</summary>
    internal static string Local(string address) => address
        .Replace("://[::]", "://localhost", StringComparison.Ordinal)
        .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal)
        .Replace("://+", "://localhost", StringComparison.Ordinal)
        .Replace("://*", "://localhost", StringComparison.Ordinal);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Warmed up in {Milliseconds} ms; ready for traffic")]
        public static partial void WarmedUp(ILogger logger, long milliseconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The warm-up didn't finish; ready for traffic anyway")]
        public static partial void GaveUp(ILogger logger, Exception exception);
    }
}
