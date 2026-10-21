using EventTicketing.Inventory.Application.Holds;
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

        // Browsing needs no account. Uses the database clock like the claim, but it's still only
        // advice: by the time the fan asks for a seat, someone else may have it.
        events.MapGet("/availability", async (Guid eventId, InventoryDbContext db, CancellationToken cancellationToken) =>
            {
                // DateTimeOffset.UtcNow inside a query is translated to now(), the database clock.
                var free = db.Seats.AsNoTracking().Where(seat => seat.EventId == eventId
                    && (seat.Status == SeatStatus.Available || (seat.Status == SeatStatus.Held && seat.HeldUntil < DateTimeOffset.UtcNow)));

                var sections = await db.Seats.AsNoTracking()
                    .Where(seat => seat.EventId == eventId)
                    .GroupBy(seat => seat.Section)
                    .OrderBy(section => section.Key)
                    .Select(section => new SectionAvailability(
                        section.Key,
                        section.Count(seat => seat.Status == SeatStatus.Available || (seat.Status == SeatStatus.Held && seat.HeldUntil < DateTimeOffset.UtcNow)),
                        section.Count()))
                    .ToListAsync(cancellationToken);

                var seatIds = await free.Where(seat => seat.Row != null).Select(seat => seat.SeatId).ToListAsync(cancellationToken);

                var onSale = await db.SalesWindows.AnyAsync(
                    w => w.EventId == eventId && w.OnSaleAt <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow < w.SalesEndAt,
                    cancellationToken);

                return TypedResults.Ok(new Availability(eventId, onSale, sections, seatIds));
            })
            .AllowAnonymous()
            .WithName("GetAvailability")
            .WithSummary("Free places per section, and which seats are free");

        events.MapPost("/holds", async (Guid eventId, PlaceHoldRequest request, ICommandHandler<PlaceHold, HoldPlaced> handler, CancellationToken cancellationToken) =>
            {
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
