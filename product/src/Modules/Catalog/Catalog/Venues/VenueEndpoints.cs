using EventTicketing.Catalog.Browse;
using EventTicketing.Catalog.Persistence;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Infrastructure.Caching;
using EventTicketing.Shared.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Catalog.Venues;

public sealed record VenueImported(Guid VenueId);

public sealed record VenueSectionSummary(string Code, string Name, string Kind, int Rows, int Places);

public sealed record VenueSummary(Guid VenueId, Guid OrganizerId, string Name, string TimeZone, int Places, IReadOnlyList<VenueSectionSummary> Sections);

internal static class VenueEndpoints
{
    /// <summary>Organizers import their venues' seat maps in the back office.</summary>
    public static void MapManagement(IEndpointRouteBuilder endpoints)
    {
        // A venue's change reaches its events' pages: the shared cache's answers go (D52).
        var venues = endpoints.MapGroup("/venues").WithTags("Venues").EvictsCached(BrowseEndpoints.CacheTag);

        venues.MapPost("/", async (ImportVenue command, ICommandHandler<ImportVenue, Guid> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(command, cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/venues/{result.Value}", new VenueImported(result.Value))
                    : result.Error.ToProblem();
            })
            .WithName("ImportVenue")
            .WithSummary("Import a venue's seat map: seated sections with rows, and standing areas")
            .Produces<VenueImported>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        venues.MapGet("/{venueId:guid}", async (Guid venueId, CatalogDbContext db, IExecutionContext caller, CancellationToken cancellationToken) =>
            {
                var venue = await db.Venues
                    .AsNoTracking()
                    .Include(v => v.Sections).ThenInclude(s => s.Seats)
                    .AsSplitQuery()
                    .SingleOrDefaultAsync(v => v.Id == venueId, cancellationToken);

                if (venue is null || !caller.OrganizerIds.Contains(venue.OrganizerId))
                {
                    return Results.NotFound();
                }

                var sections = venue.Sections
                    .OrderBy(s => s.Position)
                    .Select(s => new VenueSectionSummary(s.Code, s.Name, s.Kind.ToString(), s.Seats.Select(seat => seat.Row).Distinct().Count(), s.PlaceCount))
                    .ToList();

                return Results.Ok(new VenueSummary(venue.Id, venue.OrganizerId, venue.Name, venue.TimeZone, sections.Sum(s => s.Places), sections));
            })
            .WithName("GetVenue")
            .WithSummary("One of your venues: its sections and how many places each has")
            .Produces<VenueSummary>()
            .Produces(StatusCodes.Status404NotFound);
    }
}
