using EventTicketing.Catalog.Browse;
using EventTicketing.Catalog.Persistence;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Caching;
using EventTicketing.Shared.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Catalog.Events;

public sealed record EventCreated(Guid EventId);

public sealed record ScheduleOnSaleRequest(DateTimeOffset OnSaleAt);

public sealed record ChangePricesRequest(IReadOnlyList<PriceChange> Prices);

public sealed record EventPriceCategory(Guid PriceCategoryId, string Name, long PriceMinorUnits);

public sealed record SeatingRule(string Section, string? Row, Guid PriceCategoryId);

public sealed record EventDetails(
    Guid EventId,
    Guid OrganizerId,
    Guid VenueId,
    string Name,
    DateTimeOffset StartsAt,
    string Currency,
    string Status,
    string SalesPhase,
    DateTimeOffset? OnSaleAt,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<EventPriceCategory> PriceCategories,
    IReadOnlyList<SeatingRule> Seating);

internal static class EventEndpoints
{
    /// <summary>Organizers manage their events in the back office.</summary>
    public static void MapManagement(IEndpointRouteBuilder endpoints)
    {
        // A change here changes what fans browse: the shared cache's answers go (D52).
        var events = endpoints.MapGroup("/events").WithTags("Events").EvictsCached(BrowseEndpoints.CacheTag);

        events.MapPost("/", async (CreateEvent command, ICommandHandler<CreateEvent, Guid> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(command, cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/events/{result.Value}", new EventCreated(result.Value))
                    : result.Error.ToProblem();
            })
            .WithName("CreateEvent")
            .WithSummary("Create an event in Draft at one of your venues, with price categories and which seats are in each")
            .Produces<EventCreated>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        events.MapGet("/{eventId:guid}", async (Guid eventId, CatalogDbContext db, IExecutionContext caller, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var @event = await db.Events
                    .AsNoTracking()
                    .Include(e => e.PriceCategories)
                    .Include(e => e.PriceAssignments)
                    .AsSplitQuery()
                    .SingleOrDefaultAsync(e => e.Id == eventId, cancellationToken);

                // Other organizers' events don't exist as far as this caller is concerned.
                if (@event is null || !caller.OrganizerIds.Contains(@event.OrganizerId))
                {
                    return Results.NotFound();
                }

                return Results.Ok(new EventDetails(
                    @event.Id,
                    @event.OrganizerId,
                    @event.VenueId,
                    @event.Name,
                    @event.StartsAt,
                    @event.Currency,
                    @event.Status.ToString(),
                    @event.SalesPhaseAt(time.GetUtcNow()).ToString(),
                    @event.OnSaleAt,
                    @event.PublishedAt,
                    [.. @event.PriceCategories.OrderBy(c => c.Name).Select(c => new EventPriceCategory(c.Id, c.Name, c.PriceMinorUnits))],
                    [.. @event.PriceAssignments.OrderBy(a => a.Section).ThenBy(a => a.Row).Select(a => new SeatingRule(a.Section, a.Row, a.PriceCategoryId))]));
            })
            .WithName("GetEventDetails")
            .WithSummary("One of your events, as you manage it")
            .Produces<EventDetails>()
            .Produces(StatusCodes.Status404NotFound);

        events.MapPut("/{eventId:guid}/on-sale", async (Guid eventId, ScheduleOnSaleRequest request, ICommandHandler<ScheduleOnSale, Unit> handler, CancellationToken cancellationToken) =>
                Ok(await handler.HandleAsync(new ScheduleOnSale(eventId, request.OnSaleAt), cancellationToken)))
            .WithName("ScheduleOnSale")
            .WithSummary("Set when sales start; a time in the past opens sales now")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        events.MapPut("/{eventId:guid}/prices", async (Guid eventId, ChangePricesRequest request, ICommandHandler<ChangePrices, Unit> handler, CancellationToken cancellationToken) =>
                Ok(await handler.HandleAsync(new ChangePrices(eventId, request.Prices), cancellationToken)))
            .WithName("ChangePrices")
            .WithSummary("Change the price of some price categories; orders already placed keep theirs")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        events.MapPost("/{eventId:guid}/publish", async (Guid eventId, ICommandHandler<PublishEvent, Unit> handler, CancellationToken cancellationToken) =>
                Ok(await handler.HandleAsync(new PublishEvent(eventId), cancellationToken)))
            .WithName("PublishEvent")
            .WithSummary("Publish a Draft event, so its seats can be sold once sales start")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static IResult Ok(Result result) => result.IsSuccess ? Results.NoContent() : result.Error.ToProblem();
}
