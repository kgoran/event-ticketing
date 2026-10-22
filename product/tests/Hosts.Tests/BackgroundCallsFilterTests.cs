using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Hosts.Tests;

/// <summary>
/// OB1: a background loop's database and cache calls, outside any request or message, are not
/// exported; the same calls inside a request are, and so is an HTTP call that starts a trace.
/// </summary>
public sealed class BackgroundCallsFilterTests : IDisposable
{
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = _ => true,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    private readonly ActivitySource _npgsql = new("Npgsql");
    private readonly ActivitySource _http = new("System.Net.Http");
    private readonly ActivitySource _requests = new("Microsoft.AspNetCore");
    private readonly BackgroundCallsFilter _filter = new();

    public BackgroundCallsFilterTests() => ActivitySource.AddActivityListener(_listener);

    [Fact]
    public void A_query_outside_any_request_is_not_exported()
    {
        using var polling = _npgsql.StartActivity("postgresql", ActivityKind.Client)!;

        _filter.OnEnd(polling);

        polling.Recorded.Should().BeFalse("the outbox's polling would bury the requests' traces");
    }

    [Fact]
    public void A_query_inside_a_request_is_exported_with_it()
    {
        using var request = _requests.StartActivity("POST /events/{eventId}/holds", ActivityKind.Server)!;
        using var query = _npgsql.StartActivity("postgresql", ActivityKind.Client)!;

        _filter.OnEnd(query);

        query.Recorded.Should().BeTrue();
    }

    [Fact]
    public void An_http_call_that_starts_a_trace_is_exported()
    {
        using var call = _http.StartActivity("POST", ActivityKind.Client)!;

        _filter.OnEnd(call);

        call.Recorded.Should().BeTrue("the fan site's calls to PublicApi start the purchase's trace");
    }

    public void Dispose()
    {
        _filter.Dispose();
        _requests.Dispose();
        _http.Dispose();
        _npgsql.Dispose();
        _listener.Dispose();
    }
}
