using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Hosts.Tests;

/// <summary>
/// D36: every outgoing HTTP client recycles its connections, so instances added behind a Kubernetes
/// Service (which balances per connection) get traffic too.
/// </summary>
public sealed class ConnectionRecyclingTests
{
    [Fact]
    public void Every_http_client_reuses_a_connection_for_at_most_a_minute()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        using var services = builder.Services.BuildServiceProvider();

        // Any named client, the fan site's PublicApi client among them, gets the defaults.
        using var handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("publicapi");

        var primary = handler;
        while (primary is DelegatingHandler delegating)
        {
            primary = delegating.InnerHandler!;
        }

        primary.Should().BeOfType<SocketsHttpHandler>().Which.PooledConnectionLifetime.Should().Be(TimeSpan.FromMinutes(1));
    }
}
