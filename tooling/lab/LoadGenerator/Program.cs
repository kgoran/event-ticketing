using EventTicketing.Tooling.LoadGenerator;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// The Scaling Lab's load generator (ADR-0017): PUT /settings sets the rate and mix, GET /stats says
// what was asked for and what happened, second by second. Started by the AppHost at 0 requests/s.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(LabUrls.FromEnvironment());
builder.Services.AddSingleton<LoadControl>();
builder.Services.AddSingleton<LoadStats>();

// One connection pool for everything; a request that takes longer than 5 s counts as failed.
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = Pacer.MaxInFlight, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
{
    Timeout = TimeSpan.FromSeconds(5),
});
builder.Services.AddSingleton<LabFans>();
builder.Services.AddSingleton<TargetEvents>();
builder.Services.AddSingleton<Traffic>();
builder.Services.AddHostedService<Pacer>();

// The generator's requests carry trace context, and only 1 in 20 is sampled: the product's hosts
// follow the caller's decision (parent-based sampling), so under load the dashboard keeps a sample
// of traces instead of drowning in them. Metrics still count every request (docs/observability.md).
builder.Logging.AddOpenTelemetry(logging => logging.IncludeFormattedMessage = true);
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(LoadSettings.TracedShare)))
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics.AddHttpClientInstrumentation());
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

var app = builder.Build();

app.MapHealthChecks("/health");

app.MapGet("/settings", (LoadControl control) => control.Current);

app.MapPut("/settings", (LoadSettings settings, LoadControl control) =>
{
    if (settings.RequestsPerSecond is < 0 or > LoadSettings.MaxRate || settings.HoldShare is < 0 or > 1)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["settings"] = [$"The rate is 0 to {LoadSettings.MaxRate} requests/s, the hold share 0 to 1."],
        });
    }

    control.Set(settings);
    return Results.Ok(settings);
});

app.MapGet("/stats", (int? seconds, LoadControl control, LoadStats stats, TargetEvents events) =>
    stats.Snapshot(control.Current, events.Current(control.Current.EventId), seconds ?? 120));

app.Run();

/// <summary>Lets tests start the generator with WebApplicationFactory.</summary>
public partial class Program;
