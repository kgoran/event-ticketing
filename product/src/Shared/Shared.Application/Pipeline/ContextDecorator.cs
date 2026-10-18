using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using Microsoft.Extensions.Logging;

namespace EventTicketing.Shared.Application.Pipeline;

/// <summary>
/// Step 1, outermost: establishes who is acting, so every log line below carries the command,
/// the user and the organizer context (ADR-0023).
/// </summary>
internal sealed class ContextDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IExecutionContext context,
    ILoggerFactory loggerFactory)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger(CommandTelemetry.Name);

    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["Command"] = typeof(TCommand).Name,
            ["UserId"] = context.UserId,
            ["OrganizerIds"] = context.OrganizerIds.Count > 0 ? string.Join(',', context.OrganizerIds) : null,
        });

        return await inner.HandleAsync(command, cancellationToken);
    }
}
