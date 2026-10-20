using EventTicketing.Shared.Application.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Shared.Infrastructure.Authentication;

/// <summary>The claims the product reads from access tokens. Any OIDC provider that issues them works (ADR-0027).</summary>
public static class TokenClaims
{
    public const string Subject = "sub";

    /// <summary>A flat list of roles: fan, organizer, admin, door-staff.</summary>
    public const string Roles = "roles";

    /// <summary>One value per organizer the caller is a member of (ADR-0023).</summary>
    public const string OrganizerId = "organizer_id";

    public const string UserName = "preferred_username";

    public const string Email = "email";
}

public sealed record CallerResponse(string UserId, IReadOnlyList<string> Roles, IReadOnlyList<Guid> OrganizerIds);

public static class ApiAuthentication
{
    /// <summary>
    /// JWT bearer authentication with standard OIDC discovery (configuration section "Authentication":
    /// Authority, Audience, RequireHttpsMetadata), and a fallback policy that requires a signed-in
    /// caller on every endpoint that doesn't explicitly allow anonymous access.
    /// </summary>
    public static IHostApplicationBuilder AddApiAuthentication(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = builder.Configuration.GetSection("Authentication");

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = settings["Authority"];
                options.Audience = settings["Audience"];
                options.RequireHttpsMetadata = settings.GetValue("RequireHttpsMetadata", true);

                // Keep the token's own claim names ("sub", "roles") instead of mapping them to long .NET URIs.
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = TokenClaims.UserName;
                options.TokenValidationParameters.RoleClaimType = TokenClaims.Roles;
            });

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return builder;
    }

    /// <summary>GET /me: who the access token says you are. Useful for demos and for checking a token.</summary>
    public static IEndpointConventionBuilder MapCallerEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapGet("/me", (IExecutionContext caller) => TypedResults.Ok(new CallerResponse(
                caller.UserId ?? string.Empty,
                [.. caller.Roles.Order(StringComparer.Ordinal)],
                [.. caller.OrganizerIds.Order()])))
            .WithName("GetCaller")
            .WithSummary("Who the access token says you are");
}
