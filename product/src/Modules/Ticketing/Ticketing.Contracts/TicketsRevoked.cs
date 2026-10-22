using EventTicketing.Shared.Contracts;

namespace EventTicketing.Ticketing.Contracts;

/// <summary>
/// Tickets that no longer let anyone in (D63): revoked by an admin now; by a chargeback or a
/// cancelled event in M4. CheckIn adds them to the door list's revocations, so the devices refuse
/// them, offline too, once they have synced.
/// </summary>
[IntegrationEvent("ticketing.tickets-revoked", 1)]
public sealed record TicketsRevoked(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid EventId,
    IReadOnlyList<Guid> TicketIds,
    string Reason) : IIntegrationEvent;
