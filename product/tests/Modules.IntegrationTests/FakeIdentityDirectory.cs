using System.Collections.Concurrent;
using EventTicketing.Organizers.Identity;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>Keycloak's groups as Organizers sees them through its port; can be made unreachable for one organizer.</summary>
internal sealed class FakeIdentityDirectory : IIdentityDirectory
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>> _groups = new();
    private readonly ConcurrentDictionary<Guid, bool> _unreachableFor = new();

    public Task EnsureOrganizerGroupAsync(Guid organizerId, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable(organizerId);
        _groups.GetOrAdd(organizerId, _ => new ConcurrentDictionary<string, bool>());
        return Task.CompletedTask;
    }

    public Task SetMembershipAsync(Guid organizerId, string userId, bool isMember, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable(organizerId);
        var members = _groups.GetOrAdd(organizerId, _ => new ConcurrentDictionary<string, bool>());
        if (isMember)
        {
            members[userId] = true;
        }
        else
        {
            members.TryRemove(userId, out _);
        }

        return Task.CompletedTask;
    }

    public bool HasGroup(Guid organizerId) => _groups.ContainsKey(organizerId);

    public IReadOnlyCollection<string> MembersOf(Guid organizerId) =>
        _groups.TryGetValue(organizerId, out var members) ? [.. members.Keys.Order(StringComparer.Ordinal)] : [];

    public void MakeUnreachableFor(Guid organizerId) => _unreachableFor[organizerId] = true;

    public void MakeReachableFor(Guid organizerId) => _unreachableFor.TryRemove(organizerId, out _);

    private void ThrowIfUnreachable(Guid organizerId)
    {
        if (_unreachableFor.ContainsKey(organizerId))
        {
            throw new HttpRequestException("Keycloak can't be reached.");
        }
    }
}
