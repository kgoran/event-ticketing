using EventTicketing.Shared.Contracts;

namespace EventTicketing.Organizers.Contracts;

/// <summary>An organizer was registered on the platform (Reporting keeps a list of them).</summary>
[IntegrationEvent("organizers.organizer-created", 1)]
public sealed record OrganizerCreated(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid OrganizerId,
    string Name) : IIntegrationEvent;
