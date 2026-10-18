using EventTicketing.Inventory.Domain;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using FluentValidation;

namespace EventTicketing.Inventory.Application.Holds;

/// <summary>A fan holds some seats of an event, all or none (ADR-0011).</summary>
public sealed record PlaceHold(Guid EventId, IReadOnlyList<Guid> SeatIds) : ICommand<HoldPlaced>;

public sealed record HoldPlaced(Guid HoldId, DateTimeOffset ExpiresAt, IReadOnlyList<Guid> SeatIds);

internal sealed class PlaceHoldValidator : AbstractValidator<PlaceHold>
{
    public PlaceHoldValidator(InventoryPolicy policy)
    {
        RuleFor(command => command.EventId).NotEmpty();
        RuleFor(command => command.SeatIds).NotEmpty()
            .Must(seats => seats.Count <= policy.MaxSeatsPerHold).WithMessage($"Hold at most {policy.MaxSeatsPerHold} seats at a time.")
            .Must(seats => seats.Distinct().Count() == seats.Count).WithMessage("Each seat may appear only once.");
        RuleForEach(command => command.SeatIds).NotEmpty();
    }
}

internal sealed class PlaceHoldAuthorizer : RoleAuthorizer<PlaceHold>
{
    protected override string Role => Roles.Fan;
}

internal sealed class PlaceHoldHandler(
    IHoldRepository holds,
    ISeatInventory seats,
    IExecutionContext caller,
    TimeProvider time,
    InventoryPolicy policy)
    : ICommandHandler<PlaceHold, HoldPlaced>
{
    public async Task<Result<HoldPlaced>> HandleAsync(PlaceHold command, CancellationToken cancellationToken)
    {
        var customerId = caller.UserId ?? throw new InvalidOperationException("A hold needs a signed-in caller.");
        var hold = Hold.Place(command.EventId, customerId, command.SeatIds, time.GetUtcNow(), policy.HoldLifetime);

        // A partial claim is rolled back with the transaction when this returns a failure.
        if (!await seats.TryClaimAsync(command.EventId, command.SeatIds, hold.Id, policy.HoldLifetime, cancellationToken))
        {
            return Error.Conflict("hold.seats_unavailable", "One or more of these seats are no longer available.");
        }

        holds.Add(hold);
        return new HoldPlaced(hold.Id, hold.ExpiresAt, hold.SeatIds);
    }
}
