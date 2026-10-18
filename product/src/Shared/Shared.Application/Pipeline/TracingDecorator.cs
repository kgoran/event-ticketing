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
        string? errorCode = null;

        try
        {
            var result = await inner.HandleAsync(command, cancellationToken);

            outcome = result.IsSuccess ? "success" : "failure";
            if (result.IsFailure)
            {
                errorCode = result.Error.Code;
                activity?.SetTag("eventticketing.error.code", errorCode);
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

            // Error codes are a small, fixed set per command, e.g. hold.seats_unavailable: safe as a tag.
            if (errorCode is not null)
            {
                tags.Add("eventticketing.error.code", errorCode);
            }

            CommandTelemetry.Handled.Add(1, tags);
            CommandTelemetry.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }
}
