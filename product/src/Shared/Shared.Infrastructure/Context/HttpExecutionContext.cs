using System.Security.Claims;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;

namespace EventTicketing.Shared.Infrastructure.Context;

/// <summary>The caller of an HTTP request, read from the validated access token and request headers.</summary>
internal sealed class HttpExecutionContext(IHttpContextAccessor accessor) : IExecutionContext
{
    internal const string IdempotencyKeyHeader = "Idempotency-Key";

    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public string? UserId => User?.FindFirstValue(TokenClaims.Subject);

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public string? Email => User?.FindFirstValue(TokenClaims.Email);

    public IReadOnlySet<string> Roles =>
        User is null
            ? EmptySet<string>.Instance
            : User.Identities.SelectMany(identity => identity.FindAll(identity.RoleClaimType)).Select(c => c.Value).ToHashSet();

    public IReadOnlySet<Guid> OrganizerIds =>
        User is null
            ? EmptySet<Guid>.Instance
            : User.FindAll(TokenClaims.OrganizerId)
                .Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToHashSet();

    public string? IdempotencyKey =>
        accessor.HttpContext?.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var value) == true
            ? value.ToString()
            : null;
}

/// <summary>
/// Work started by the system itself: consumed messages, scheduled messages, migrations. The
/// relay and scheduler set the idempotency key to the message ID, so commands a handler runs are
/// idempotent per message (ADR-0033).
/// </summary>
internal sealed class MessageExecutionContext : IExecutionContext
{
    public string? UserId => null;

    public bool IsAuthenticated => false;

    public string? Email => null;

    public IReadOnlySet<string> Roles => EmptySet<string>.Instance;

    public IReadOnlySet<Guid> OrganizerIds => EmptySet<Guid>.Instance;

    public string? IdempotencyKey { get; set; }
}

internal static class EmptySet<T>
{
    public static readonly IReadOnlySet<T> Instance = new HashSet<T>();
}
