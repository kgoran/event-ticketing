using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Inventory.Application.Holds;

/// <summary>Moves the caller's hold into checkout for an order (ADR-0009, step 1). Called through IInventoryApi only.</summary>
public sealed record BeginCheckout(Guid HoldId, Guid OrderId) : ICommand<CheckoutSeats>;

public sealed record CheckoutSeats(Guid EventId, IReadOnlyList<SeatOfHold> Seats);

internal sealed class BeginCheckoutAuthorizer : InternalCommandAuthorizer<BeginCheckout>;

internal sealed class BeginCheckoutHandler(
    IHoldRepository holds,
    ISeatInventory seats,
    IExecutionContext caller,
    TimeProvider time,
    InventoryPolicy policy)
    : ICommandHandler<BeginCheckout, CheckoutSeats>
{
    public async Task<Result<CheckoutSeats>> HandleAsync(BeginCheckout command, CancellationToken cancellationToken)
    {
        var hold = await holds.FindAsync(command.HoldId, cancellationToken);
        if (hold is null)
        {
            return Error.NotFound("hold.not_found", "The hold does not exist.");
        }

        var begun = hold.BeginCheckout(command.OrderId, caller.UserId ?? string.Empty, time.GetUtcNow() + policy.CheckoutBackstop);
        if (begun.IsFailure)
        {
            return begun.Error;
        }

        // The seat rows decide (database clock): the hold must still own every seat and not have expired.
        if (!await seats.TryExtendAsync(hold.Id, hold.SeatIds.Count, policy.CheckoutBackstop, cancellationToken))
        {
            return Error.Conflict("hold.expired", "The hold has expired.");
        }

        return new CheckoutSeats(hold.EventId, await seats.SeatsOfHoldAsync(hold.Id, cancellationToken));
    }
}

public enum HoldConfirmation
{
    Confirmed,
    SeatsLost,
}

/// <summary>Sells a paid hold's seats to its order (ADR-0009). Called through IInventoryApi only. Safe to repeat.</summary>
public sealed record ConfirmHold(Guid HoldId, Guid OrderId) : ICommand<HoldConfirmation>;

internal sealed class ConfirmHoldAuthorizer : InternalCommandAuthorizer<ConfirmHold>;

internal sealed class ConfirmHoldHandler(IHoldRepository holds, ISeatInventory seats)
    : ICommandHandler<ConfirmHold, HoldConfirmation>
{
    public async Task<Result<HoldConfirmation>> HandleAsync(ConfirmHold command, CancellationToken cancellationToken)
    {
        var hold = await holds.FindAsync(command.HoldId, cancellationToken);
        if (hold is null)
        {
            return Error.NotFound("hold.not_found", "The hold does not exist.");
        }

        if (hold.IsConfirmedFor(command.OrderId))
        {
            return HoldConfirmation.Confirmed;
        }

        // Even an expired hold can still be sold if nobody has taken its seats in the meantime.
        if (await seats.TrySellAsync(hold.Id, command.OrderId, hold.SeatIds.Count, cancellationToken))
        {
            hold.Confirm(command.OrderId);
            return HoldConfirmation.Confirmed;
        }

        hold.MarkSeatsLost();
        return HoldConfirmation.SeatsLost;
    }
}
