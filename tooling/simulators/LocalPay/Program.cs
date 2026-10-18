using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// LocalPay: a payment provider simulator. For now it only starts and reports healthy, so the
// AppHost can wire it in; sessions, the hosted page, webhooks and scenarios come with L1.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Logging.AddOpenTelemetry(logging => logging.IncludeFormattedMessage = true);
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation());

if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapGet("/", () => "LocalPay payment simulator. The payment APIs arrive with backlog item L1.");

app.Run();
