using EventTicketing.Inventory.Domain;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using FluentValidation;

namespace EventTicketing.Inventory.Application.Holds;

/// <summary>
/// A fan holds places of an event, all or none (ADR-0011): either the seats they picked, or the
/// best <paramref name="Quantity"/> free places of a <paramref name="Section"/>.
/// </summary>
public sealed record PlaceHold(Guid EventId, IReadOnlyList<Guid>? SeatIds, string? Section = null, int? Quantity = null) : ICommand<HoldPlaced>;

public sealed record HoldPlaced(Guid HoldId, DateTimeOffset ExpiresAt, IReadOnlyList<Guid> SeatIds);

internal sealed class PlaceHoldValidator : AbstractValidator<PlaceHold>
{
    public PlaceHoldValidator(InventoryPolicy policy)
    {
        RuleFor(command => command.EventId).NotEmpty();
        RuleFor(command => command)
            .Must(command => command.SeatIds is { Count: > 0 } != (command.Section is not null || command.Quantity is not null))
            .WithName("Seats")
            .WithMessage("Pick seats, or give a section and a quantity for the best available.");

        When(command => command.SeatIds is { Count: > 0 }, () =>
        {
            RuleFor(command => command.SeatIds!)
                .Must(seats => seats.Count <= policy.MaxSeatsPerHold).WithMessage($"Hold at most {policy.MaxSeatsPerHold} seats at a time.")
                .Must(seats => seats.Distinct().Count() == seats.Count).WithMessage("Each seat may appear only once.");
            RuleForEach(command => command.SeatIds).NotEmpty();
        });

        When(command => command.Section is not null || command.Quantity is not null, () =>
        {
            RuleFor(command => command.Section).NotEmpty().MaximumLength(50);
            RuleFor(command => command.Quantity).NotNull().InclusiveBetween(1, policy.MaxSeatsPerHold)
                .WithMessage($"Hold 1 to {policy.MaxSeatsPerHold} places at a time.");
        });
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

        // One current hold per fan per event. Two requests at the same moment can both pass this
        // check; the unique index then refuses the second at commit (InventoryDbContext).
        if (await holds.FindCurrentAsync(command.EventId, customerId, cancellationToken) is { } current)
        {
            return Error.Conflict(
                "hold.active_hold_exists",
                $"You already hold seats for this event (hold {current.Id}). Release it or check out first.");
        }

        var holdId = Guid.CreateVersion7();
        var claim = command.SeatIds is { Count: > 0 } seatIds
            ? await seats.TryClaimAsync(command.EventId, seatIds, holdId, policy.HoldLifetime, cancellationToken)
            : await seats.TryClaimBestAsync(command.EventId, command.Section!, command.Quantity!.Value, holdId, policy.HoldLifetime, cancellationToken);

        // A partial claim is rolled back with the transaction when this returns a failure. Why it
        // failed is asked only now, so a successful hold stays a single claim statement.
        if (claim is null)
        {
            return await seats.IsOnSaleAsync(command.EventId, cancellationToken)
                ? Error.Conflict("hold.seats_unavailable", "One or more of these places are no longer available.")
                : Error.Conflict("hold.not_on_sale", "This event isn't on sale.");
        }

        var hold = Hold.Place(holdId, command.EventId, customerId, claim.SeatIds, time.GetUtcNow(), claim.HeldUntil);
        holds.Add(hold);
        return new HoldPlaced(hold.Id, hold.ExpiresAt, hold.SeatIds);
    }
}
