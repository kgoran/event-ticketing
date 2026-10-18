using System.Collections.Concurrent;
using System.Diagnostics;

namespace EventTicketing.Shared.IntegrationTests;

public sealed class TracingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_consumer_spans_join_the_trace_of_the_request_that_published_the_event()
    {
        var finished = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("EventTicketing", StringComparison.Ordinal) || source.Name == "Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = finished.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var tests = new ActivitySource("Tests");

        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());

        ActivityTraceId traceId;
        using (var request = tests.StartActivity("request"))
        {
            traceId = request!.TraceId;
            await app.PlaceOrderAsync("Ana");
        }

        await app.RelayOnceAsync();

        finished
            .Where(span => span.TraceId == traceId && span.OperationName == "consume sales.order-placed")
            .Should().HaveCount(2, "both Warehouse consumers continue the request's trace");
    }
}
