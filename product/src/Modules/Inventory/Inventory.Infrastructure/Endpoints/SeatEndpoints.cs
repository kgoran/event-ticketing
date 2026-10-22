using EventTicketing.Inventory.Application.Holds;
using EventTicketing.Inventory.Infrastructure.Availability;
using EventTicketing.Inventory.Infrastructure.Persistence;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Features;
using EventTicketing.Shared.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Inventory.Infrastructure.Endpoints;

public sealed record SectionAvailability(string Section, int Available, int Total);

/// <param name="AvailableSeatIds">Free seats, to join with the seat map; standing slots are only counted per section.</param>
public sealed record Availability(Guid EventId, bool OnSale, IReadOnlyList<SectionAvailability> Sections, IReadOnlyList<Guid> AvailableSeatIds);

/// <summary>Either the seats a fan picked, or a section and how many of its best free places.</summary>
public sealed record PlaceHoldRequest(IReadOnlyList<Guid>? SeatIds = null, string? Section = null, int? Quantity = null);

internal static class SeatEndpoints
{
    public static void MapForFans(IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapGroup("/events/{eventId:guid}").WithTags("Seats");

        // Browsing needs no account. From the snapshot when there is one (D53): one query a second for
        // everyone watching an on-sale, instead of three per request. Otherwise from a replica (D75):
        // it is advice, the hold decides; and without the primary, during a failover, it still answers.
        events.MapGet("/availability", async (Guid eventId, InventoryReplicaDbContext db, AvailabilitySnapshots snapshots, CancellationToken cancellationToken) =>
                TypedResults.Ok(await snapshots.ReadAsync(eventId) ?? await AvailabilityQuery.RunAsync(db, eventId, cancellationToken)))
            .AllowAnonymous()
            .WithName("GetAvailability")
            .WithSummary("Free places per section, and which seats are free");

        events.MapPost("/holds", async (Guid eventId, PlaceHoldRequest request, ICommandHandler<PlaceHold, HoldPlaced> handler, AvailabilitySnapshots snapshots, CancellationToken cancellationToken) =>
            {
                // A sold-out section is refused at once, without the database (ADR-0011's lever 2, D53).
                // The flag is a second old at most; the claim would have refused too.
                if (request.Section is { } section && request.SeatIds is not { Count: > 0 } && await snapshots.IsSoldOutAsync(eventId, section))
                {
                    return Error.Conflict("hold.seats_unavailable", "This section is sold out for now.").ToProblem();
                }

                var result = await handler.HandleAsync(new PlaceHold(eventId, request.SeatIds, request.Section, request.Quantity), cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/holds/{result.Value.HoldId}", result.Value)
                    : result.Error.ToProblem();
            })
            .WithName("PlaceHold")
            .WithSummary("Hold places for a few minutes, all or none: the seats you picked, or the best free places of a section")
            .Produces<HoldPlaced>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .PausedBy(
                FeatureFlags.PauseHolds,
                "New holds are paused for a moment. Please try again shortly.",
                "An operator has paused new holds. Places held already stay yours, and checkouts go on.");

        endpoints.MapDelete("/holds/{holdId:guid}", async (Guid holdId, ICommandHandler<ReleaseHold, Unit> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new ReleaseHold(holdId), cancellationToken);
                return result.IsSuccess ? Results.NoContent() : result.Error.ToProblem();
            })
            .WithTags("Seats")
            .WithName("ReleaseHold")
            .WithSummary("Let go of your hold before checkout; its places are free again at once")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
