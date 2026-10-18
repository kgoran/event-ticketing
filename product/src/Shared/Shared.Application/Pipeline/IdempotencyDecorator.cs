using System.Text.Json;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Application.Idempotency;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.Application.Pipeline;

/// <summary>
/// Step 4: a repeated idempotency key returns the first outcome instead of running the command
/// again (ADR-0009). Requests without a key run normally. Both successes and business failures
/// are stored; after an exception the key is released so the client can retry.
/// </summary>
internal sealed class IdempotencyDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IExecutionContext context,
    IIdempotencyStore store)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        if (context.IdempotencyKey is not { Length: > 0 } key)
        {
            return await inner.HandleAsync(command, cancellationToken);
        }

        var claim = await store.TryClaimAsync(key, typeof(TCommand), cancellationToken);

        switch (claim)
        {
            case IdempotencyClaim.AlreadyCompleted completed:
                return Replay(completed.Result);
            case IdempotencyClaim.InProgress:
                return Error.Conflict(
                    "idempotency.in_progress",
                    "A request with this idempotency key is still being processed.");
        }

        Result<TResult> result;
        try
        {
            result = await inner.HandleAsync(command, cancellationToken);
        }
        catch
        {
            await store.ReleaseAsync(key, typeof(TCommand), CancellationToken.None);
            throw;
        }

        await store.CompleteAsync(key, typeof(TCommand), ToStored(result), cancellationToken);
        return result;
    }

    private static StoredResult ToStored(Result<TResult> result) =>
        result.IsSuccess
            ? new StoredResult(true, JsonSerializer.Serialize(result.Value), null, null, null)
            : new StoredResult(false, null, result.Error.Code, result.Error.Message, result.Error.Type);

    private static Result<TResult> Replay(StoredResult stored) =>
        stored.IsSuccess
            ? Result.Success(JsonSerializer.Deserialize<TResult>(stored.ValueJson ?? "null")!)
            : new Error(stored.ErrorCode ?? "unknown", stored.ErrorMessage ?? string.Empty, stored.ErrorType ?? ErrorType.Failure);
}
