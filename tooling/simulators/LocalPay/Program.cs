using EventTicketing.Tooling.LocalPay;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// LocalPay: our payment provider simulator (ADR-0004). It behaves like a hosted checkout: create a
// payment, show a page with Approve and Decline, send a signed webhook, redirect back to the shop.
// State is in memory; the lab is disposable anyway (ADR-0022). Failure scenarios come with L1.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Logging.AddOpenTelemetry(logging => logging.IncludeFormattedMessage = true);
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation());

if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection("Webhooks"));
builder.Services.AddSingleton<PaymentStore>();
builder.Services.AddHttpClient<WebhookSender>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapGet("/", () => "LocalPay payment simulator. POST /v1/payments to start a payment.");

app.MapPost("/v1/payments", (CreatePayment request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, PaymentStore store, HttpRequest http) =>
{
    var payment = store.Create(request, idempotencyKey, $"{http.Scheme}://{http.Host}");
    return Results.Created($"/v1/payments/{payment.Id}", payment.ToResponse());
});

app.MapGet("/v1/payments/{paymentId}", (string paymentId, PaymentStore store) =>
    store.Find(paymentId) is { } payment ? Results.Ok(payment.ToResponse()) : Results.NotFound());

app.MapGet("/checkout/{paymentId}", (string paymentId, PaymentStore store) =>
    store.Find(paymentId) is { } payment
        ? Results.Content(CheckoutPage.Render(payment), "text/html")
        : Results.NotFound());

app.MapPost("/checkout/{paymentId}/approve", (string paymentId, PaymentStore store, WebhookSender webhooks, CancellationToken cancellationToken) =>
    CompleteAsync(paymentId, succeeded: true, store, webhooks, cancellationToken));

app.MapPost("/checkout/{paymentId}/decline", (string paymentId, PaymentStore store, WebhookSender webhooks, CancellationToken cancellationToken) =>
    CompleteAsync(paymentId, succeeded: false, store, webhooks, cancellationToken));

app.Run();

static async Task<IResult> CompleteAsync(string paymentId, bool succeeded, PaymentStore store, WebhookSender webhooks, CancellationToken cancellationToken)
{
    if (store.Complete(paymentId, succeeded) is not { } payment)
    {
        return Results.NotFound();
    }

    await webhooks.SendAsync(payment, cancellationToken);
    return Results.Redirect(payment.ReturnUrl);
}
