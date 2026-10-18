using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using EventTicketing.Orders.Application.Checkout;
using EventTicketing.Orders.Infrastructure;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1a Q3: one trace from POST /orders through Payments, the outbox and Workers to the confirmation;
/// the hold and the webhook requests are linked to it. And the first business metrics.
/// </summary>
public sealed class ObservabilityTests(TestSystem system)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_purchase_is_one_trace_from_checkout_to_confirmation_with_the_hold_linked()
    {
        using var spans = new SpanCapture();
        var sale = await system.OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());

        var holdTrace = ActivityTraceId.CreateRandom();
        using var holdRequest = new HttpRequestMessage(HttpMethod.Post, $"/events/{sale.EventId}/holds")
        {
            Content = JsonContent.Create(new { seatIds = new[] { sale.Seats[0] } }),
        };
        holdRequest.Headers.Add("traceparent", $"00-{holdTrace}-{ActivitySpanId.CreateRandom()}-01");
        var hold = await (await ana.SendAsync(holdRequest, Ct)).PlacedAsync();

        // The fan's checkout request starts the purchase's trace.
        var purchase = ActivityTraceId.CreateRandom();
        using var checkoutRequest = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(new StartCheckoutRequest(hold.HoldId)) };
        checkoutRequest.Headers.Add("traceparent", $"00-{purchase}-{ActivitySpanId.CreateRandom()}-01");
        var checkoutResponse = await ana.SendAsync(checkoutRequest, Ct);
        checkoutResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var checkout = (await checkoutResponse.Content.ReadFromJsonAsync<CheckoutStarted>(Ct))!;

        // The provider's webhook is a separate request, with its own trace.
        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "succeeded");
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();
        (await ana.OrderAsync(checkout.OrderId)).Status.Should().Be("Confirmed");

        var trace = spans.InTrace(purchase);
        trace.Select(span => span.DisplayName).Should().Contain(
        [
            "command StartCheckout",
            "command BeginCheckout",
            "command CreateOrder",
            "command CreateCheckout",
            "payment succeeded",
            "consume payments.payment-succeeded",
            "command ConfirmHold",
        ]);

        var beginCheckout = trace.Single(span => span.DisplayName == "command BeginCheckout");
        beginCheckout.Links.Select(link => link.Context.TraceId).Should().Contain(holdTrace, "the hold's request is linked");

        var paymentSucceeded = trace.Single(span => span.DisplayName == "payment succeeded");
        paymentSucceeded.Links.Should().ContainSingle("the webhook's own trace is linked")
            .Which.Context.TraceId.Should().NotBe(purchase);
    }

    [Fact]
    public async Task Hold_results_and_order_transitions_are_measured()
    {
        using var metrics = new MetricCapture();
        var sale = await system.OnSaleAsync();
        using var ana = system.PublicApi(Callers.Fan());
        using var ben = system.PublicApi(Callers.Fan());

        var hold = await (await ana.HoldAsync(sale.EventId, sale.Seats[0], sale.Seats[1])).PlacedAsync();
        (await ben.HoldAsync(sale.EventId, sale.Seats[1])).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var checkout = await ana.CheckoutAsync(hold.HoldId);
        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "succeeded");
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();

        metrics.Sum("eventticketing.commands.handled", ("eventticketing.command", "PlaceHold"), ("eventticketing.error.code", "hold.seats_unavailable"))
            .Should().BeGreaterThanOrEqualTo(1);
        metrics.Sum("eventticketing.inventory.places_held").Should().BeGreaterThanOrEqualTo(2);
        metrics.Sum("eventticketing.payments.webhooks", ("eventticketing.webhook.result", "stored")).Should().BeGreaterThanOrEqualTo(1);
        metrics.Sum("eventticketing.payments.outcomes", ("eventticketing.payment.status", "succeeded")).Should().BeGreaterThanOrEqualTo(1);
        metrics.Sum("eventticketing.orders.transitions", ("eventticketing.order.status", "confirmed")).Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>Records finished spans from the product's sources and ASP.NET Core, as the OpenTelemetry exporter would see them.</summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _spans = new();
        private readonly ActivityListener _listener;

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name.StartsWith("EventTicketing.", StringComparison.Ordinal) || source.Name == "Microsoft.AspNetCore",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _spans.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> InTrace(ActivityTraceId traceId) => [.. _spans.Where(span => span.TraceId == traceId)];

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Adds up the product's counters while it lives (other tests running at the same time may add to them too).</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly ConcurrentQueue<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _measurements = new();
        private readonly MeterListener _listener = new();

        public MetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name.StartsWith("EventTicketing.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _measurements.Enqueue((instrument.Name, value, tags.ToArray())));
            _listener.Start();
        }

        public long Sum(string name, params (string Key, string Value)[] tags) =>
            _measurements
                .Where(m => m.Name == name && tags.All(tag => m.Tags.Any(t => t.Key == tag.Key && Equals(t.Value, tag.Value))))
                .Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }
}
