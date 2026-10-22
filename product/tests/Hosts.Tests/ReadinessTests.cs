using System.Net;

namespace EventTicketing.Hosts.Tests;

/// <summary>
/// D74: readiness says whether this instance can serve, never whether a dependency every instance
/// shares is up. With the database in it, a failover took every instance out of rotation at once,
/// and browsing, which needs no primary, failed with the writes.
/// </summary>
public sealed class ReadinessTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(AuthenticationTests.ApiHosts), MemberType = typeof(AuthenticationTests))]
    public async Task An_instance_is_ready_while_the_database_and_valkey_are_unreachable(string host)
    {
        await using var api = HostUnderTest.Start(
            host,
            ("ConnectionStrings:eventticketing", "Host=127.0.0.1;Port=1;Database=eventticketing;Username=nobody;Timeout=1"),
            ("ConnectionStrings:eventticketing-replica", "Host=127.0.0.1;Port=1;Database=eventticketing;Username=nobody;Timeout=1"),
            ("ConnectionStrings:valkey", "127.0.0.1:1,abortConnect=false,connectTimeout=200"));

        // The warm-up finishes at once in memory (no address to warm up through); until then, 503.
        var status = HttpStatusCode.ServiceUnavailable;
        for (var attempt = 0; attempt < 50 && status != HttpStatusCode.OK; attempt++)
        {
            status = (await api.Client.GetAsync(new Uri("/health", UriKind.Relative), CancellationToken)).StatusCode;
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken);
        }

        status.Should().Be(HttpStatusCode.OK);
    }
}
