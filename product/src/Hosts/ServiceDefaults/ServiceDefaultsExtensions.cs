using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

public static class ServiceDefaultsExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    // Every product source and meter is named "EventTicketing.<area>", e.g. EventTicketing.Commands.
    private const string ProductTelemetry = "EventTicketing.*";

    /// <summary>How long an outgoing HTTP connection is reused before a new one is opened.</summary>
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(1);

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.AddFeatureFlags();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // A Kubernetes Service balances per connection, not per request: kept connections would
            // send everything to the instances reached first and none to new ones (D36, ADR-0019).
            http.UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = PooledConnectionLifetime);
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(ProductTelemetry))
            .WithTracing(tracing => tracing
                // Before the exporter, which skips what this marks as not recorded.
                .AddProcessor(new BackgroundCallsFilter())
                .AddSource(builder.Environment.ApplicationName)
                .AddSource(ProductTelemetry)
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                        && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                        && !context.Request.Headers.ContainsKey(WarmUpService.Header))
                .AddHttpClientInstrumentation());

        // Aspire, and later the lab, set the OTLP endpoint; without it nothing is exported.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Feature flags (FF1, ADR-0036): the "FeatureManagement" section of the configuration, through
    /// Microsoft.FeatureManagement. Where FeatureFlags:File names a file (in Kubernetes, a ConfigMap
    /// mounted into every instance), it is read too and re-read when it changes, so a flag switches
    /// in a running system without a deploy.
    /// </summary>
    public static TBuilder AddFeatureFlags<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        if (builder.Configuration["FeatureFlags:File"] is { Length: > 0 } file)
        {
            builder.Configuration.AddJsonFile(file, optional: true, reloadOnChange: true);
        }

        builder.Services.AddFeatureManagement();
        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Warms the host up before it reports ready (D43): once it listens, <paramref name="routine"/>
    /// sends it requests, and /health stays unhealthy until that has finished (or given up after a
    /// minute). /alive isn't affected, so a slow warm-up never gets the host restarted.
    /// </summary>
    public static TBuilder AddWarmUp<TBuilder>(this TBuilder builder, WarmUpRoutine routine)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddSingleton(routine);
        builder.Services.AddSingleton<WarmUpState>();
        builder.Services.AddHostedService<WarmUpService>();
        builder.Services.AddHealthChecks().AddCheck<WarmUpHealthCheck>("warm-up");

        return builder;
    }

    /// <summary>
    /// Maps /health (ready: every check passes) and /alive (live: only checks tagged "live").
    /// Mapped in every environment, because Kubernetes probes use them from M2 on, and open to
    /// anonymous callers even where every other endpoint needs a token.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath).AllowAnonymous();
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
        }).AllowAnonymous();

        return app;
    }
}
