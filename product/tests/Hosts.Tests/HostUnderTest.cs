using EventTicketing.BackOfficeApi;
using EventTicketing.PublicApi;
using EventTicketing.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace EventTicketing.Hosts.Tests;

/// <summary>An API host running in memory, accepting tokens from <see cref="TestTokens"/>.</summary>
internal sealed class HostUnderTest : IAsyncDisposable
{
    private readonly IAsyncDisposable _factory;

    private HostUnderTest(IAsyncDisposable factory, HttpClient client, IServiceProvider services)
    {
        _factory = factory;
        Client = client;
        Services = services;
    }

    public HttpClient Client { get; }

    public IServiceProvider Services { get; }

    public static HostUnderTest Start(string host) => host switch
    {
        "PublicApi" => Start(new WebApplicationFactory<PublicApiEntryPoint>()),
        "BackOfficeApi" => Start(new WebApplicationFactory<BackOfficeApiEntryPoint>()),
        _ => throw new ArgumentOutOfRangeException(nameof(host), host, "Unknown host."),
    };

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }

    private static HostUnderTest Start<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
    {
        var configured = factory.WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureTestServices(services => services.UseTestTokens());
        });

        return new HostUnderTest(factory, configured.CreateClient(), configured.Services);
    }
}
