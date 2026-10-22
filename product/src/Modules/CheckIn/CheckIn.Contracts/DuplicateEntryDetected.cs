using EventTicketing.Shared.Contracts;

namespace EventTicketing.CheckIn.Contracts;

/// <summary>
/// A ticket let in a second time (ADR-0013): two doors admitted it, offline or before they synced.
/// The first admission counts; this one is an alert, for staff to look into (a copied code, a
/// fan who went out and back). Nobody consumes it yet; Reporting will count them (M4).
/// </summary>
[IntegrationEvent("checkin.duplicate-entry-detected", 1)]
public sealed record DuplicateEntryDetected(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid EventId,
    Guid TicketId,
    Guid FirstDeviceId,
    DateTimeOffset FirstAdmittedAt,
    Guid ScanId,
    Guid DeviceId,
    DateTimeOffset ScannedAt) : IIntegrationEvent;
