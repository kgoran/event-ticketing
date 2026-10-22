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

    /// <param name="settings">Configuration for the host, such as connection strings.</param>
    public static HostUnderTest Start(string host, params (string Key, string Value)[] settings) => host switch
    {
        "PublicApi" => Start(new WebApplicationFactory<PublicApiEntryPoint>(), settings),
        "BackOfficeApi" => Start(new WebApplicationFactory<BackOfficeApiEntryPoint>(), settings),
        _ => throw new ArgumentOutOfRangeException(nameof(host), host, "Unknown host."),
    };

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }

    private static HostUnderTest Start<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory, (string Key, string Value)[] settings)
        where TEntryPoint : class
    {
        var configured = factory.WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }

            host.ConfigureTestServices(services => services.UseTestTokens());
        });

        return new HostUnderTest(factory, configured.CreateClient(), configured.Services);
    }
}
