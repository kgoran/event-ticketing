using EventTicketing.Organizers.Persistence;
using EventTicketing.Shared.Application.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Organizers.Identity;

/// <summary>Create the organizer's group at the identity provider. Scheduled when the organizer is registered.</summary>
public sealed record SyncOrganizerGroup(Guid OrganizerId);

/// <summary>
/// Make the identity provider agree with Organizers about one user's membership of one organizer.
/// Scheduled whenever a member is added or removed, in the same transaction.
/// </summary>
public sealed record SyncMembership(Guid OrganizerId, string UserId);

internal sealed class SyncOrganizerGroupHandler(IIdentityDirectory directory)
    : IScheduledMessageHandler<SyncOrganizerGroup>, INonTransactionalMessageHandler
{
    public Task HandleAsync(SyncOrganizerGroup message, CancellationToken cancellationToken) =>
        directory.EnsureOrganizerGroupAsync(message.OrganizerId, cancellationToken);
}

/// <summary>
/// Reconciles instead of replaying: it reads whether the user is a member <em>now</em> and makes the
/// provider match. So it is safe to repeat, and an add and a remove arriving in either order end
/// where Organizers' record is. If the provider is down, the scheduler tries again with backoff;
/// meanwhile the user's tokens still say what they said (ADR-0027: changes show at the next sign-in).
/// </summary>
internal sealed class SyncMembershipHandler(OrganizersDbContext db, IIdentityDirectory directory)
    : IScheduledMessageHandler<SyncMembership>, INonTransactionalMessageHandler
{
    public async Task HandleAsync(SyncMembership message, CancellationToken cancellationToken)
    {
        var isMember = await db.Set<Member>().AsNoTracking()
            .AnyAsync(member => member.OrganizerId == message.OrganizerId && member.UserId == message.UserId, cancellationToken);

        await directory.SetMembershipAsync(message.OrganizerId, message.UserId, isMember, cancellationToken);
    }
}
