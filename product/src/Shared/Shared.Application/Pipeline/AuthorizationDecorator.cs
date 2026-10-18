using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.Application.Pipeline;

/// <summary>Step 2: every authorizer for the command must allow it. No authorizer means no access.</summary>
internal sealed class AuthorizationDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<ICommandAuthorizer<TCommand>> authorizers,
    IExecutionContext context)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        var anyAuthorizer = false;

        foreach (var authorizer in authorizers)
        {
            anyAuthorizer = true;

            if (!await authorizer.IsAllowedAsync(command, context, cancellationToken))
            {
                return Error.Forbidden("authorization.denied", "You are not allowed to do this.");
            }
        }

        if (!anyAuthorizer)
        {
            return Error.Forbidden(
                "authorization.not_configured",
                $"No authorizer is registered for {typeof(TCommand).Name}, so it is refused.");
        }

        return await inner.HandleAsync(command, cancellationToken);
    }
}
