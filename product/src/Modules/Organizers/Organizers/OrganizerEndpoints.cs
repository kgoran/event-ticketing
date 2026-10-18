using EventTicketing.Organizers.Persistence;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Organizers;

public sealed record OrganizerCreatedResponse(Guid OrganizerId);

public sealed record OrganizerSummary(Guid OrganizerId, string Name);

public sealed record OrganizerMember(string UserId, string DisplayName, DateTimeOffset AddedAt);

public sealed record OrganizerDetails(Guid OrganizerId, string Name, DateTimeOffset CreatedAt, IReadOnlyList<OrganizerMember> Members);

internal static class OrganizerEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var organizers = endpoints.MapGroup("/organizers").WithTags("Organizers");

        organizers.MapPost("/", async (CreateOrganizer command, ICommandHandler<CreateOrganizer, Guid> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(command, cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/organizers/{result.Value}", new OrganizerCreatedResponse(result.Value))
                    : result.Error.ToProblem();
            })
            .WithName("CreateOrganizer")
            .WithSummary("Register an organizer (admins)")
            .Produces<OrganizerCreatedResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Admins see every organizer; everyone else the ones their token says they belong to.
        organizers.MapGet("/", async (OrganizersDbContext db, IExecutionContext caller, CancellationToken cancellationToken) =>
            {
                var visible = caller.Roles.Contains(Roles.Admin)
                    ? db.Organizers
                    : db.Organizers.Where(o => caller.OrganizerIds.Contains(o.Id));

                return Results.Ok(await visible.AsNoTracking()
                    .OrderBy(o => o.Name)
                    .Select(o => new OrganizerSummary(o.Id, o.Name))
                    .ToListAsync(cancellationToken));
            })
            .WithName("ListOrganizers")
            .WithSummary("The organizers you belong to (admins: all of them)")
            .Produces<List<OrganizerSummary>>();

        organizers.MapGet("/{organizerId:guid}", async (Guid organizerId, OrganizersDbContext db, IExecutionContext caller, CancellationToken cancellationToken) =>
            {
                if (!caller.Roles.Contains(Roles.Admin) && !caller.OrganizerIds.Contains(organizerId))
                {
                    return Results.NotFound();
                }

                var organizer = await db.Organizers.AsNoTracking()
                    .Include(o => o.Members)
                    .SingleOrDefaultAsync(o => o.Id == organizerId, cancellationToken);

                return organizer is null
                    ? Results.NotFound()
                    : Results.Ok(new OrganizerDetails(
                        organizer.Id,
                        organizer.Name,
                        organizer.CreatedAt,
                        [.. organizer.Members.OrderBy(m => m.DisplayName).Select(m => new OrganizerMember(m.UserId, m.DisplayName, m.AddedAt))]));
            })
            .WithName("GetOrganizer")
            .WithSummary("An organizer and its members")
            .Produces<OrganizerDetails>()
            .Produces(StatusCodes.Status404NotFound);

        organizers.MapPost("/{organizerId:guid}/members", async (Guid organizerId, AddMemberRequest request, ICommandHandler<AddMember, Unit> handler, CancellationToken cancellationToken) =>
                NoContentOrProblem(await handler.HandleAsync(new AddMember(organizerId, request.UserId, request.DisplayName), cancellationToken)))
            .WithName("AddOrganizerMember")
            .WithSummary("Record a member (M1a: their access still comes from their Keycloak group)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        organizers.MapDelete("/{organizerId:guid}/members/{userId}", async (Guid organizerId, string userId, ICommandHandler<RemoveMember, Unit> handler, CancellationToken cancellationToken) =>
                NoContentOrProblem(await handler.HandleAsync(new RemoveMember(organizerId, userId), cancellationToken)))
            .WithName("RemoveOrganizerMember")
            .WithSummary("Remove a member from the record")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static IResult NoContentOrProblem(Result result) => result.IsSuccess ? Results.NoContent() : result.Error.ToProblem();
}
