using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;

namespace EventTicketing.Shared.Infrastructure.Features;

/// <summary>
/// The product's feature flags (FF1, ADR-0036), read from the "FeatureManagement" configuration
/// section by Microsoft.FeatureManagement. In the lab they live in a ConfigMap that every instance
/// re-reads while running, and `lab flag` switches them (docs/feature-flags.md).
/// </summary>
public static class FeatureFlags
{
    /// <summary>
    /// An operator's brake (D45): while on, new holds are refused with 503 and Retry-After, so an
    /// incident can take the heaviest load off the database without a deploy. Browsing, the holds
    /// fans have already and checkouts go on.
    /// </summary>
    public const string PauseHolds = "PauseHolds";

    /// <summary>
    /// While <paramref name="flag"/> is on, the endpoint answers 503 with Retry-After: not now, on
    /// purpose, and clients that honour Retry-After come back later. <paramref name="title"/> is
    /// what a fan reads.
    /// </summary>
    public static RouteHandlerBuilder PausedBy(this RouteHandlerBuilder endpoint, string flag, string title, string detail, int retryAfterSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint
            .AddEndpointFilter(async (context, next) =>
            {
                var features = context.HttpContext.RequestServices.GetRequiredService<IFeatureManager>();
                if (!await features.IsEnabledAsync(flag))
                {
                    return await next(context);
                }

                context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return TypedResults.Problem(
                    title: title,
                    detail: detail,
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?> { ["code"] = "paused", ["flag"] = flag });
            })
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}
