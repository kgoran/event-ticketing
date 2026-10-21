using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventTicketing.Shared.Infrastructure.Caching;

/// <summary>
/// The shared output cache (ADR-0028, D52): answers that are the same for every caller, kept in
/// Valkey, so every instance of a host serves what one of them built, and the database sees one
/// query per answer and lifetime instead of one per request. Valkey is never authoritative
/// (ADR-0003): when it is down, a lookup is a miss and a store is skipped, never an error.
/// </summary>
public static partial class SharedOutputCache
{
    /// <summary>The connection string's name, as the AppHost and the lab's chart give it.</summary>
    public const string ConnectionName = "valkey";

    /// <summary>The store: Valkey when the host has its connection string, memory otherwise (tests, a host run on its own).</summary>
    public static TBuilder AddSharedOutputCache<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(ConnectionName)))
        {
            builder.Services.AddOutputCache();
            return builder;
        }

        builder.AddRedisOutputCache(ConnectionName);

        // Wrap the Valkey store, so that an outage costs cache misses and nothing else.
        var registered = builder.Services.Last(service => service.ServiceType == typeof(IOutputCacheStore));
        builder.Services.Remove(registered);
        builder.Services.AddSingleton<IOutputCacheStore>(services => new ForgivingStore(
            (IOutputCacheStore)(registered.ImplementationInstance
                ?? registered.ImplementationFactory?.Invoke(services)
                ?? ActivatorUtilities.CreateInstance(services, registered.ImplementationType!)),
            services.GetRequiredService<ILogger<ForgivingStore>>()));
        return builder;
    }

    /// <summary>
    /// Caches the endpoint's successful answers for <paramref name="lifetime"/> under
    /// <paramref name="tags"/>, for every caller alike, signed in or not: only for answers that don't
    /// depend on who asks. ASP.NET Core's default policy skips requests with an Authorization header,
    /// and the fan site's calls carry one. A tag may name a route value in braces, such as
    /// "catalog-event:{eventId}", so a change can evict one event's answers and leave the rest.
    /// </summary>
    public static RouteHandlerBuilder CachedForEveryone(this RouteHandlerBuilder endpoint, TimeSpan lifetime, params string[] tags) =>
        endpoint.CacheOutput(new SameForEveryone(lifetime, tags));

    /// <summary>
    /// The moment this answer changes by itself (an on-sale opens, an event starts): it is cached no
    /// longer than that, and Cache-Control says the same (<see cref="CacheControl"/>). Called by the
    /// endpoint: the output cache fixes an entry's lifetime when the response starts.
    /// </summary>
    public static void ExpiresBy(HttpContext context, DateTimeOffset moment)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Features.Get<IOutputCacheFeature>()?.Context is not { } cache)
        {
            return;
        }

        var left = moment - (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        if (left <= TimeSpan.Zero)
        {
            cache.AllowCacheStorage = false;
        }
        else if (cache.ResponseExpirationTimeSpan is not { } lifetime || left < lifetime)
        {
            cache.ResponseExpirationTimeSpan = left;
        }
    }

    /// <summary>A public Cache-Control for <paramref name="lifetime"/>, or less if the answer changes sooner.</summary>
    public static string CacheControl(TimeSpan lifetime, DateTimeOffset? changes, DateTimeOffset now)
    {
        var seconds = changes is { } moment && moment - now < lifetime ? Math.Max(0, (int)(moment - now).TotalSeconds) : (int)lifetime.TotalSeconds;
        return $"public, max-age={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// After a successful change (anything but a read), every answer cached under
    /// <paramref name="tags"/> goes, so the next request builds it again: the cache's lifetime is how
    /// stale an answer may get when nothing changed, not when something did. Route values in braces
    /// are the change's own; a tag whose value the route doesn't have is skipped.
    /// </summary>
    public static TBuilder EvictsCached<TBuilder>(this TBuilder endpoints, params string[] tags)
        where TBuilder : IEndpointConventionBuilder =>
        endpoints.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            var http = context.HttpContext;
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
                && http.RequestServices.GetService<IOutputCacheStore>() is { } store
                && (result is not IStatusCodeHttpResult { StatusCode: { } status } || status < 400))
            {
                var resolved = tags.Select(tag => Resolve(tag, http)).OfType<string>().ToList();
                foreach (var tag in resolved)
                {
                    await store.EvictByTagAsync(tag, http.RequestAborted);
                }

                // Once more a second later: a replica applies a commit a moment after the primary, and a
                // request in that moment would cache the old answer again (D50).
                _ = EvictAgainAsync(store, resolved);
            }

            return result;
        });

    private static async Task EvictAgainAsync(IOutputCacheStore store, IReadOnlyList<string> tags)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        foreach (var tag in tags)
        {
            await store.EvictByTagAsync(tag, CancellationToken.None);
        }
    }

    /// <summary>A tag with its route values filled in, or null if the request's route lacks one.</summary>
    private static string? Resolve(string tag, HttpContext context)
    {
        var resolved = tag;
        foreach (System.Text.RegularExpressions.Match value in RouteValue().Matches(tag))
        {
            if (context.Request.RouteValues[value.Groups[1].Value] is not { } found)
            {
                return null;
            }

            resolved = resolved.Replace(value.Value, Convert.ToString(found, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return resolved;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{(\w+)\}")]
    private static partial System.Text.RegularExpressions.Regex RouteValue();

    private sealed class SameForEveryone(TimeSpan lifetime, string[] tags) : IOutputCachePolicy
    {
        public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
        {
            var read = HttpMethods.IsGet(context.HttpContext.Request.Method) || HttpMethods.IsHead(context.HttpContext.Request.Method);
            context.EnableOutputCaching = true;
            context.AllowCacheLookup = read;
            context.AllowCacheStorage = read;
            context.AllowLocking = true; // one request builds an expired answer; the others wait for it
            context.ResponseExpirationTimeSpan = lifetime;
            context.CacheVaryByRules.QueryKeys = "*";
            foreach (var tag in tags.Select(tag => Resolve(tag, context.HttpContext)).OfType<string>())
            {
                context.Tags.Add(tag);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

        public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
        {
            if (context.HttpContext.Response.StatusCode != StatusCodes.Status200OK || context.HttpContext.Response.Headers.SetCookie.Count > 0)
            {
                context.AllowCacheStorage = false;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed partial class ForgivingStore(IOutputCacheStore store, ILogger<ForgivingStore> logger) : IOutputCacheStore
    {
        public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
        {
            try
            {
                return await store.GetAsync(key, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Unavailable(logger, exception);
                return null;
            }
        }

        public async ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
        {
            try
            {
                await store.SetAsync(key, value, tags, validFor, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Unavailable(logger, exception);
            }
        }

        public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
        {
            try
            {
                await store.EvictByTagAsync(tag, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Unavailable(logger, exception);
            }
        }

        private static partial class Log
        {
            [LoggerMessage(Level = LogLevel.Warning, Message = "The output cache's store is unavailable; serving without it")]
            public static partial void Unavailable(ILogger logger, Exception exception);
        }
    }
}
