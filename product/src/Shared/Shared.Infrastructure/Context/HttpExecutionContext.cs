using System.Security.Claims;
using EventTicketing.Shared.Application.Context;
using Microsoft.AspNetCore.Http;

namespace EventTicketing.Shared.Infrastructure.Context;

/// <summary>The caller of an HTTP request, read from the validated access token and request headers.</summary>
internal sealed class HttpExecutionContext(IHttpContextAccessor accessor) : IExecutionContext
{
    // Claim names are settled together with the Keycloak realm (backlog item K1).
    internal const string SubjectClaim = "sub";
    internal const string OrganizerClaim = "organizer_id";
    internal const string IdempotencyKeyHeader = "Idempotency-Key";

    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public string? UserId => User?.FindFirstValue(SubjectClaim) ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public IReadOnlySet<string> Roles =>
        User is null ? EmptySet<string>.Instance : User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet();

    public IReadOnlySet<Guid> OrganizerIds =>
        User is null
            ? EmptySet<Guid>.Instance
            : User.FindAll(OrganizerClaim)
                .Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToHashSet();

    public string? IdempotencyKey =>
        accessor.HttpContext?.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var value) == true
            ? value.ToString()
            : null;
}

/// <summary>Work started by the system itself: scheduled jobs and, until messages carry their own context (F4), consumers.</summary>
internal sealed class SystemExecutionContext : IExecutionContext
{
    public string? UserId => null;

    public bool IsAuthenticated => false;

    public IReadOnlySet<string> Roles => EmptySet<string>.Instance;

    public IReadOnlySet<Guid> OrganizerIds => EmptySet<Guid>.Instance;

    public string? IdempotencyKey => null;
}

internal static class EmptySet<T>
{
    public static readonly IReadOnlySet<T> Instance = new HashSet<T>();
}
