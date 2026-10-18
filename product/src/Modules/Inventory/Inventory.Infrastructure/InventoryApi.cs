using EventTicketing.Inventory.Application.Holds;
using EventTicketing.Inventory.Contracts;
using EventTicketing.Shared.Application.Commands;

namespace EventTicketing.Inventory.Infrastructure;

/// <summary>
/// The in-process implementation of Inventory's module API. Each call runs an Inventory command
/// through the pipeline, so it gets Inventory's own transaction (ADR-0009). Extracting the module
/// later means replacing this class with a gRPC client (ADR-0001).
/// </summary>
internal sealed class InventoryApi(
    ICommandHandler<BeginCheckout, CheckoutSeats> beginCheckout,
    ICommandHandler<ConfirmHold, HoldConfirmation> confirmHold)
    : IInventoryApi
{
    public async Task<BeginCheckoutResult> BeginCheckoutAsync(Guid holdId, Guid orderId, CancellationToken cancellationToken)
    {
        var result = await beginCheckout.HandleAsync(new BeginCheckout(holdId, orderId), cancellationToken);

        return result.IsSuccess
            ? new BeginCheckoutResult(
                BeginCheckoutOutcome.Started,
                result.Value.EventId,
                [.. result.Value.Seats.Select(seat => new HeldSeat(seat.SeatId, seat.PriceCategoryId))])
            : new BeginCheckoutResult(BeginCheckoutOutcome.HoldNotActive, Guid.Empty, []);
    }

    public async Task<ConfirmHoldResult> ConfirmHoldAsync(Guid holdId, Guid orderId, CancellationToken cancellationToken)
    {
        var result = await confirmHold.HandleAsync(new ConfirmHold(holdId, orderId), cancellationToken);

        // A paid order whose hold doesn't exist is a bug, not a business outcome: fail loudly so the
        // message is retried and, if it keeps failing, dead-lettered.
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Could not confirm hold {holdId} for order {orderId}: {result.Error.Code}.");
        }

        return new ConfirmHoldResult(result.Value == HoldConfirmation.Confirmed ? ConfirmHoldOutcome.Confirmed : ConfirmHoldOutcome.SeatsLost);
    }
}
