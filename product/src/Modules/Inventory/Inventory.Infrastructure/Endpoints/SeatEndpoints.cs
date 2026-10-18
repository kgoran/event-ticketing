using EventTicketing.Inventory.Application.Holds;
using EventTicketing.Inventory.Infrastructure.Persistence;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Inventory.Infrastructure.Endpoints;

public sealed record SeatAvailability(Guid SeatId, string Section, string Row, int Number, Guid PriceCategoryId, bool Available);

public sealed record PlaceHoldRequest(IReadOnlyList<Guid> SeatIds);

internal static class SeatEndpoints
{
    public static void MapForFans(IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapGroup("/events/{eventId:guid}").WithTags("Seats");

        // Browsing needs no account. Availability is advisory (application clock); the hold decides.
        events.MapGet("/seats", async (Guid eventId, InventoryDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var now = time.GetUtcNow();
                var seats = await db.Seats.AsNoTracking()
                    .Where(seat => seat.EventId == eventId)
                    .OrderBy(seat => seat.Section).ThenBy(seat => seat.Row).ThenBy(seat => seat.Number)
                    .Select(seat => new SeatAvailability(
                        seat.SeatId,
                        seat.Section,
                        seat.Row,
                        seat.Number,
                        seat.PriceCategoryId,
                        seat.Status == SeatStatus.Available || (seat.Status == SeatStatus.Held && seat.HeldUntil < now)))
                    .ToListAsync(cancellationToken);

                return TypedResults.Ok(seats);
            })
            .AllowAnonymous()
            .WithName("GetSeats")
            .WithSummary("Seats of an event and whether each is available");

        events.MapPost("/holds", async (Guid eventId, PlaceHoldRequest request, ICommandHandler<PlaceHold, HoldPlaced> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new PlaceHold(eventId, request.SeatIds), cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/holds/{result.Value.HoldId}", result.Value)
                    : result.Error.ToProblem();
            })
            .WithName("PlaceHold")
            .WithSummary("Hold seats for a few minutes, all or none")
            .Produces<HoldPlaced>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
