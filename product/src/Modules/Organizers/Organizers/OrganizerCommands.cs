using EventTicketing.Organizers.Contracts;
using EventTicketing.Organizers.Persistence;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Organizers;

/// <summary>
/// An admin registers an organizer. Also the request body of POST /organizers.
/// </summary>
/// <param name="OrganizerId">
/// Optional: the ID of a group that already exists in Keycloak, so the realm's organizer and this
/// record match. Until M1b creates Keycloak groups from here, that's how an organizer gets members.
/// </param>
public sealed record CreateOrganizer(string Name, Guid? OrganizerId = null) : ICommand<Guid>;

internal sealed class CreateOrganizerValidator : AbstractValidator<CreateOrganizer>
{
    public CreateOrganizerValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.OrganizerId).NotEqual(Guid.Empty);
    }
}

internal sealed class CreateOrganizerAuthorizer : RoleAuthorizer<CreateOrganizer>
{
    protected override string Role => Roles.Admin;
}

internal sealed class CreateOrganizerHandler(OrganizersDbContext db, IIntegrationEventPublisher events, TimeProvider time)
    : ICommandHandler<CreateOrganizer, Guid>
{
    public async Task<Result<Guid>> HandleAsync(CreateOrganizer command, CancellationToken cancellationToken)
    {
        var id = command.OrganizerId ?? Guid.CreateVersion7();
        if (await db.Organizers.AnyAsync(o => o.Id == id, cancellationToken))
        {
            return Error.Conflict("organizer.exists", "An organizer with this ID already exists.");
        }

        var now = time.GetUtcNow();
        var organizer = Organizer.Create(id, command.Name, now);
        db.Organizers.Add(organizer);
        events.Publish(new OrganizerCreated(Guid.CreateVersion7(), now, organizer.Id, organizer.Name));
        return organizer.Id;
    }
}

/// <param name="UserId">The user's subject in Keycloak (the sub claim of their tokens).</param>
public sealed record AddMemberRequest(string UserId, string DisplayName);

internal sealed record AddMember(Guid OrganizerId, string UserId, string DisplayName) : ICommand<Unit>;

internal sealed class AddMemberValidator : AbstractValidator<AddMember>
{
    public AddMemberValidator()
    {
        RuleFor(command => command.UserId).NotEmpty().MaximumLength(200);
        RuleFor(command => command.DisplayName).NotEmpty().MaximumLength(200);
    }
}

internal sealed record RemoveMember(Guid OrganizerId, string UserId) : ICommand<Unit>;

/// <summary>Admins and the organizer's own members manage its members (ADR-0023).</summary>
internal abstract class MemberOrAdminAuthorizer<TCommand> : ICommandAuthorizer<TCommand>
{
    public ValueTask<bool> IsAllowedAsync(TCommand command, IExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(
            context.IsAuthenticated && (context.Roles.Contains(Roles.Admin) || context.OrganizerIds.Contains(OrganizerIdOf(command))));
    }

    protected abstract Guid OrganizerIdOf(TCommand command);
}

internal sealed class AddMemberAuthorizer : MemberOrAdminAuthorizer<AddMember>
{
    protected override Guid OrganizerIdOf(AddMember command) => command.OrganizerId;
}

internal sealed class RemoveMemberAuthorizer : MemberOrAdminAuthorizer<RemoveMember>
{
    protected override Guid OrganizerIdOf(RemoveMember command) => command.OrganizerId;
}

internal sealed class AddMemberHandler(OrganizersDbContext db, TimeProvider time) : ICommandHandler<AddMember, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddMember command, CancellationToken cancellationToken)
    {
        var organizer = await db.Organizers.Include(o => o.Members).SingleOrDefaultAsync(o => o.Id == command.OrganizerId, cancellationToken);
        if (organizer is null)
        {
            return Error.NotFound("organizer.not_found", "The organizer does not exist.");
        }

        var added = organizer.AddMember(command.UserId, command.DisplayName, time.GetUtcNow());
        return added.IsSuccess ? Unit.Value : added.Error;
    }
}

internal sealed class RemoveMemberHandler(OrganizersDbContext db) : ICommandHandler<RemoveMember, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RemoveMember command, CancellationToken cancellationToken)
    {
        var organizer = await db.Organizers.Include(o => o.Members).SingleOrDefaultAsync(o => o.Id == command.OrganizerId, cancellationToken);
        if (organizer is null)
        {
            return Error.NotFound("organizer.not_found", "The organizer does not exist.");
        }

        var removed = organizer.RemoveMember(command.UserId);
        return removed.IsSuccess ? Unit.Value : removed.Error;
    }
}
