using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Hosts.Tests;

/// <summary>
/// AS2: the warm-up's requests are left out of the HTTP metrics the autoscaler reads, so a new
/// instance warming up doesn't look like load; other requests are counted.
/// </summary>
public sealed class WarmUpMetricsTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_warm_up_request_is_not_counted_and_any_other_is()
    {
        await using var api = HostUnderTest.Start("PublicApi");
        var routes = new List<string?>();
        var meters = api.Services.GetRequiredService<IMeterFactory>();
        using var listener = new MeterListener
        {
            // This host's request durations only: other tests run hosts at the same time.
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Scope == meters && instrument.Name == "http.server.request.duration")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            var route = default(string);
            foreach (var tag in tags)
            {
                if (tag.Key == "http.route")
                {
                    route = tag.Value as string;
                }
            }

            lock (routes)
            {
                routes.Add(route);
            }
        });
        listener.Start();

        using var warmUp = new HttpRequestMessage(HttpMethod.Get, new Uri("/alive", UriKind.Relative));
        warmUp.Headers.Add("X-Warm-Up", "1");
        await api.Client.SendAsync(warmUp, CancellationToken);
        await api.Client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), CancellationToken);

        // A request's duration is recorded once its response has gone, a moment after the client has it.
        for (var waited = 0; waited < 50 && Count(routes) == 0; waited++)
        {
            await Task.Delay(100, CancellationToken);
        }

        await Task.Delay(200, CancellationToken);
        lock (routes)
        {
            routes.Should().Equal(["/openapi/{documentName}.json"], "the warm-up isn't traffic, and a request from anyone else is");
        }
    }

    private static int Count(List<string?> routes)
    {
        lock (routes)
        {
            return routes.Count;
        }
    }
}
