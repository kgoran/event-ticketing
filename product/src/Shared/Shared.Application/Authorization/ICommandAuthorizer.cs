using EventTicketing.Shared.Application.Context;

namespace EventTicketing.Shared.Application.Authorization;

/// <summary>
/// Decides whether the caller may run a command. Every command needs at least one authorizer:
/// a command without one is refused (fail closed), so forgetting authorization is a visible bug
/// rather than a silent hole.
/// </summary>
public interface ICommandAuthorizer<in TCommand>
{
    ValueTask<bool> IsAllowedAsync(TCommand command, IExecutionContext context, CancellationToken cancellationToken);
}
