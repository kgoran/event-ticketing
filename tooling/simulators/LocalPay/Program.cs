using EventTicketing.Tooling.LocalPay;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// LocalPay: our payment provider simulator (ADR-0004, docs/payments.md). It behaves like a hosted
// checkout: create a payment, show a page with Approve and Decline, redirect back to the shop and
// send a signed webhook. Payments can be expired early, refunded and asked for their status, and
// misbehave on demand through their amount (Scenarios). State is in memory (ADR-0022).
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "LocalPay";
    document.Info.Description = ApiDescription.Text;
    return Task.CompletedTask;
}));

builder.Logging.AddOpenTelemetry(logging => logging.IncludeFormattedMessage = true);
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation());

if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection("Webhooks"));
builder.Services.Configure<ScenarioOptions>(builder.Configuration.GetSection("Scenarios"));
builder.Services.Configure<HeadlessOptions>(builder.Configuration.GetSection("Headless"));
builder.Services.AddSingleton<PaymentStore>();
builder.Services.AddSingleton<WebhookQueue>();
builder.Services.AddSingleton<Webhooks>();
builder.Services.AddSingleton<HeadlessPayer>();
builder.Services.AddHttpClient(WebhookDispatcher.ClientName);
builder.Services.AddHostedService<WebhookDispatcher>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapOpenApi();
app.MapGet("/", () => "LocalPay payment simulator. The API is described at /openapi/v1.json.").ExcludeFromDescription();

var payments = app.MapGroup("/v1/payments").WithTags("Payments");

payments.MapPost("/", (CreatePayment request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, PaymentStore store, HeadlessPayer headless, HttpRequest http) =>
    {
        var (payment, created) = store.Create(request, idempotencyKey, $"{http.Scheme}://{http.Host}");
        if (created)
        {
            headless.PayLater(payment);
        }

        return TypedResults.Created($"/v1/payments/{payment.Id}", payment.ToResponse());
    })
    .WithName("CreatePayment")
    .WithSummary("Start a payment; send the fan to its checkoutUrl. A repeated Idempotency-Key returns the same payment.");

payments.MapGet("/{paymentId}", Results<Ok<PaymentResponse>, NotFound> (string paymentId, PaymentStore store) =>
        store.Find(paymentId) is { } payment ? TypedResults.Ok(payment.ToResponse()) : TypedResults.NotFound())
    .WithName("GetPayment")
    .WithSummary("A payment's current status");

payments.MapPost("/{paymentId}/expire", Results<Ok<PaymentResponse>, Conflict<PaymentResponse>, NotFound> (string paymentId, PaymentStore store, Webhooks webhooks) =>
    {
        var (payment, change) = store.Expire(paymentId);
        if (payment is null)
        {
            return TypedResults.NotFound();
        }

        if (change == Change.Changed)
        {
            webhooks.PaymentChanged(payment);
        }

        // Like Stripe expiring a Checkout Session: only an open payment can be expired.
        return payment.Status == PaymentStatus.Expired ? TypedResults.Ok(payment.ToResponse()) : TypedResults.Conflict(payment.ToResponse());
    })
    .WithName("ExpirePayment")
    .WithSummary("Stop an open payment from being paid. 409 with the payment if it already succeeded or failed; repeating it is fine.");

var refunds = app.MapGroup("/v1/refunds").WithTags("Refunds");

refunds.MapPost("/", Results<Created<RefundResponse>, Ok<RefundResponse>, ProblemHttpResult> (CreateRefund request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, PaymentStore store, Webhooks webhooks) =>
    {
        var outcome = store.Refund(request, idempotencyKey);
        if (outcome.Refund is not { } refund)
        {
            return TypedResults.Problem(outcome.Problem, statusCode: StatusCodes.Status409Conflict);
        }

        if (outcome.Repeated)
        {
            return TypedResults.Ok(refund.ToResponse());
        }

        webhooks.Refunded(refund);
        return TypedResults.Created($"/v1/refunds/{refund.Id}", refund.ToResponse());
    })
    .WithName("CreateRefund")
    .WithSummary("Refund a succeeded payment, fully or partly. Refunds succeed at once and send refund.succeeded.");

refunds.MapGet("/{refundId}", Results<Ok<RefundResponse>, NotFound> (string refundId, PaymentStore store) =>
        store.FindRefund(refundId) is { } refund ? TypedResults.Ok(refund.ToResponse()) : TypedResults.NotFound())
    .WithName("GetRefund")
    .WithSummary("A refund");

// The hosted payment page: for the fan's browser, not part of the API.
var checkout = app.MapGroup("/checkout/{paymentId}").ExcludeFromDescription();

checkout.MapGet("/", (string paymentId, PaymentStore store) =>
    store.Find(paymentId) is { } payment
        ? Results.Content(CheckoutPage.Render(payment), "text/html")
        : Results.NotFound());

checkout.MapPost("/approve", (string paymentId, PaymentStore store, Webhooks webhooks) => Complete(paymentId, approved: true, store, webhooks));

checkout.MapPost("/decline", (string paymentId, PaymentStore store, Webhooks webhooks) => Complete(paymentId, approved: false, store, webhooks));

app.Run();

static IResult Complete(string paymentId, bool approved, PaymentStore store, Webhooks webhooks)
{
    var (payment, change) = store.Complete(paymentId, approved);
    if (payment is null)
    {
        return Results.NotFound();
    }

    if (change == Change.Changed)
    {
        webhooks.PaymentChanged(payment);
    }

    // Back to the shop either way; its return page asks its own backend what happened.
    return Results.Redirect(payment.ReturnUrl);
}

/// <summary>The API document's description: the parts a shop integrating with LocalPay must know.</summary>
internal static class ApiDescription
{
    public const string Text = """
        A payment provider simulator for development and tests. No real money moves.

        **Flow.** Create a payment and send the fan to its `checkoutUrl`. The fan approves or declines;
        LocalPay redirects to `returnUrl` and sends a webhook. Only the webhook (or the status API) says
        what happened: the redirect proves nothing.

        **Webhooks** are POSTed to the configured URL as `{ id, type, created, data }`, with types
        `payment.succeeded`, `payment.failed` and `refund.succeeded`. Header
        `LocalPay-Signature: t={unix seconds},v1={hex HMAC-SHA256 of "t.body" with the shared secret}`.
        A webhook is retried with backoff until the shop answers 2xx; a duplicate repeats the event `id`.

        **Expiry.** A payment can't be paid after `expiresAt`, and can be expired early while still open.

        **Scenarios** are chosen by the last two digits of the amount in minor units: .01 approving
        declines; .02 the webhook is late; .03 the webhook is sent twice; .04 no webhook; .05 expiring
        the payment races with the fan's payment, which wins.
        """;
}
