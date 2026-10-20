using EventTicketing.Shared.Domain;

namespace EventTicketing.Organizers;

/// <summary>
/// An organizer and its members: the source of truth on membership (ADR-0027). Keycloak puts the
/// organizer IDs in the login token, and Organizers keeps Keycloak's groups in step with this list
/// (SyncMembership), so a change shows at the member's next sign-in.
/// </summary>
internal sealed class Organizer
{
    private readonly List<Member> _members = [];

    private Organizer()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<Member> Members => _members;

    public static Organizer Create(Guid id, string name, DateTimeOffset now) => new()
    {
        Id = id,
        Name = name,
        CreatedAt = now.ToUniversalTime(),
    };

    public Result AddMember(string userId, string displayName, DateTimeOffset now)
    {
        if (_members.Any(m => m.UserId == userId))
        {
            return Result.Failure(Error.Conflict("organizer.already_member", "The user is already a member."));
        }

        _members.Add(new Member(Id, userId, displayName, now.ToUniversalTime()));
        return Result.Success();
    }

    public Result RemoveMember(string userId)
    {
        var member = _members.SingleOrDefault(m => m.UserId == userId);
        if (member is null)
        {
            return Result.Failure(Error.NotFound("organizer.not_a_member", "The user is not a member."));
        }

        _members.Remove(member);
        return Result.Success();
    }
}

/// <param name="UserId">The member's subject in the identity provider (the token's sub claim).</param>
internal sealed record Member(Guid OrganizerId, string UserId, string DisplayName, DateTimeOffset AddedAt);
