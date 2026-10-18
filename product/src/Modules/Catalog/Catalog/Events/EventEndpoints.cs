using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventTicketing.Catalog.Events;

public sealed record EventCreated(Guid EventId);

internal static class EventEndpoints
{
    /// <summary>Organizers manage their events in the back office.</summary>
    public static void MapManagement(IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapGroup("/events").WithTags("Events");

        events.MapPost("/", async (CreateEvent command, ICommandHandler<CreateEvent, Guid> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(command, cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/events/{result.Value}", new EventCreated(result.Value))
                    : result.Error.ToProblem();
            })
            .WithName("CreateEvent")
            .WithSummary("Create an event in Draft, with its price categories and seat layout")
            .Produces<EventCreated>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        events.MapPost("/{eventId:guid}/publish", async (Guid eventId, ICommandHandler<PublishEvent, Unit> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new PublishEvent(eventId), cancellationToken);
                return result.IsSuccess ? Results.NoContent() : result.Error.ToProblem();
            })
            .WithName("PublishEvent")
            .WithSummary("Publish a Draft event, so its seats can be sold")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
