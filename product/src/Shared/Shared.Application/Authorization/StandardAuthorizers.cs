using EventTicketing.Shared.Application.Context;

namespace EventTicketing.Shared.Application.Authorization;

/// <summary>
/// For commands reachable only through a module API, never from an endpoint: the calling module has
/// already authorized its caller (ADR-0007). Derive one sealed class per command, so allowing a
/// command stays an explicit, visible choice.
/// </summary>
public abstract class InternalCommandAuthorizer<TCommand> : ICommandAuthorizer<TCommand>
{
    public ValueTask<bool> IsAllowedAsync(TCommand command, IExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}

/// <summary>Allows signed-in callers that have <see cref="Role"/>. Derive one sealed class per command.</summary>
public abstract class RoleAuthorizer<TCommand> : ICommandAuthorizer<TCommand>
{
    protected abstract string Role { get; }

    public ValueTask<bool> IsAllowedAsync(TCommand command, IExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(context.IsAuthenticated && context.Roles.Contains(Role));
    }
}

/// <summary>The roles in access tokens (ADR-0027).</summary>
public static class Roles
{
    public const string Fan = "fan";
    public const string Organizer = "organizer";
    public const string Admin = "admin";
    public const string DoorStaff = "door-staff";
}
