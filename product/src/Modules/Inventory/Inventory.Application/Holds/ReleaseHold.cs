using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Inventory.Application.Holds;

/// <summary>A fan lets go of their hold before checkout, freeing its seats at once.</summary>
public sealed record ReleaseHold(Guid HoldId) : ICommand<Unit>;

internal sealed class ReleaseHoldAuthorizer : RoleAuthorizer<ReleaseHold>
{
    protected override string Role => Roles.Fan;
}

internal sealed class ReleaseHoldHandler(IHoldRepository holds, ISeatInventory seats, IExecutionContext caller)
    : ICommandHandler<ReleaseHold, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ReleaseHold command, CancellationToken cancellationToken)
    {
        // Someone else's hold is reported as missing: hold IDs reveal nothing about other fans.
        var hold = await holds.FindAsync(command.HoldId, cancellationToken);
        if (hold is null || hold.CustomerId != caller.UserId)
        {
            return Error.NotFound("hold.not_found", "The hold does not exist.");
        }

        var released = hold.Release();
        if (released.IsFailure)
        {
            return released.Error;
        }

        await seats.ReleaseAsync(hold.Id, cancellationToken);
        return Unit.Value;
    }
}
