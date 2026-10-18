using System.Diagnostics;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.Application.Pipeline;

/// <summary>
/// Step 7, innermost: a span and metrics for the handler itself. The HTTP request or consumed
/// message around it already has its own span from OpenTelemetry's instrumentation.
/// </summary>
internal sealed class TracingDecorator<TCommand, TResult>(ICommandHandler<TCommand, TResult> inner)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    private static readonly string CommandName = typeof(TCommand).Name;

    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        using var activity = CommandTelemetry.ActivitySource.StartActivity($"command {CommandName}");
        activity?.SetTag("eventticketing.command", CommandName);

        var started = Stopwatch.GetTimestamp();
        var outcome = "exception";

        try
        {
            var result = await inner.HandleAsync(command, cancellationToken);

            outcome = result.IsSuccess ? "success" : "failure";
            if (result.IsFailure)
            {
                activity?.SetTag("eventticketing.error.code", result.Error.Code);
            }

            return result;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
        finally
        {
            var tags = new TagList
            {
                { "eventticketing.command", CommandName },
                { "eventticketing.outcome", outcome },
            };

            CommandTelemetry.Handled.Add(1, tags);
            CommandTelemetry.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }
}
