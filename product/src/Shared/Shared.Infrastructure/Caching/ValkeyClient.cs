using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace EventTicketing.Shared.Infrastructure.Caching;

/// <summary>
/// Valkey's client (StackExchange.Redis, through Aspire's integration: traced, not in readiness, D74)
/// for a module that keeps something there itself, such as Inventory's availability snapshots
/// (D53). Nothing is registered without the connection string, so code asks for it with
/// GetService and works without Valkey, as everything Valkey holds may be lost (ADR-0003).
/// </summary>
public static class ValkeyClient
{
    public static TBuilder AddValkeyClient<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(SharedOutputCache.ConnectionName))
            && !builder.Services.Any(service => service.ServiceType == typeof(IConnectionMultiplexer)))
        {
            builder.AddRedisClient(SharedOutputCache.ConnectionName, settings => settings.DisableHealthChecks = true);
        }

        return builder;
    }
}
