using EventTicketing.CheckIn.Contracts;
using EventTicketing.CheckIn.Domain;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Domain;
using FluentValidation;

namespace EventTicketing.CheckIn.Application.Scanning;

/// <summary>
/// A device uploads the scans it decided at the door (ADR-0013), in a batch, whenever it is online.
/// Safe to repeat: a scan already uploaded gets the outcome it had. The first admission of a ticket
/// counts; a later one is a duplicate, and DuplicateEntryDetected raises it.
/// </summary>
/// <param name="EventId">The event the device's token is for.</param>
/// <param name="DeviceId">The device, from its token.</param>
public sealed record UploadScans(Guid EventId, Guid DeviceId, IReadOnlyList<ScanUpload> Scans) : ICommand<IReadOnlyList<ScanResult>>;

/// <summary>One scan: the device makes its ID, so a repeated upload is recognised.</summary>
/// <param name="Admitted">What the device decided: let in, or turned away.</param>
/// <param name="Reason">Why it turned the ticket away: "revoked", "unknown", "bad-code", "already-scanned".</param>
public sealed record ScanUpload(Guid ScanId, Guid TicketId, DateTimeOffset ScannedAt, bool Admitted, string? Reason = null);

/// <summary>How the server saw a scan; for a duplicate, when and where the ticket was let in first.</summary>
/// <param name="Outcome">Admitted, Duplicate or Refused (<see cref="ScanOutcome"/>).</param>
public sealed record ScanResult(Guid ScanId, string Outcome, Guid? FirstDeviceId = null, DateTimeOffset? FirstAdmittedAt = null);

internal sealed class UploadScansValidator : AbstractValidator<UploadScans>
{
    public const int MaxScans = 500;

    public UploadScansValidator()
    {
        RuleFor(command => command.Scans).NotEmpty().Must(scans => scans.Count <= MaxScans).WithMessage($"At most {MaxScans} scans per upload.");
        RuleForEach(command => command.Scans).ChildRules(scan =>
        {
            scan.RuleFor(s => s.ScanId).NotEqual(Guid.Empty);
            scan.RuleFor(s => s.TicketId).NotEqual(Guid.Empty);
            scan.RuleFor(s => s.Reason).MaximumLength(100);
        });
    }
}

internal sealed class UploadScansAuthorizer : RoleAuthorizer<UploadScans>
{
    protected override string Role => Roles.DoorStaff;
}

internal sealed class UploadScansHandler(
    IDevices devices,
    IDoorList doorList,
    IScanLog scans,
    IIntegrationEventPublisher publisher,
    TimeProvider time)
    : ICommandHandler<UploadScans, IReadOnlyList<ScanResult>>
{
    public async Task<Result<IReadOnlyList<ScanResult>>> HandleAsync(UploadScans command, CancellationToken cancellationToken)
    {
        var device = await devices.FindAsync(command.DeviceId, cancellationToken);
        if (device is not { IsActive: true } || device.EventId != command.EventId)
        {
            return Error.Forbidden("device.not_active", "This device is revoked, or isn't registered for this event.");
        }

        // One upload at a time per event: a scan uploaded twice at once is still one scan, and
        // two devices admitting the same ticket at once still make one admission (D71).
        await doorList.LockAsync(command.EventId, cancellationToken);

        var now = time.GetUtcNow();
        var results = new List<ScanResult>(command.Scans.Count);
        foreach (var upload in command.Scans)
        {
            if (await scans.FindAsync(upload.ScanId, cancellationToken) is { } known)
            {
                results.Add(await ResultOfAsync(known, cancellationToken));
                continue;
            }

            var outcome = !upload.Admitted ? ScanOutcome.Refused
                : await doorList.TryAdmitAsync(command.EventId, upload.TicketId, device.Id, upload.ScannedAt, cancellationToken) ? ScanOutcome.Admitted
                : ScanOutcome.Duplicate;

            var scan = Scan.Record(upload.ScanId, command.EventId, device.Id, upload.TicketId, upload.ScannedAt, upload.Admitted, upload.Reason, outcome, now);
            scans.Add(scan);
            var result = await ResultOfAsync(scan, cancellationToken);
            results.Add(result);
            CheckInTelemetry.Scans.Add(1, new KeyValuePair<string, object?>("eventticketing.scan.outcome", outcome.ToString().ToLowerInvariant()));

            if (outcome == ScanOutcome.Duplicate)
            {
                publisher.Publish(new DuplicateEntryDetected(
                    Guid.CreateVersion7(), now, command.EventId, upload.TicketId,
                    result.FirstDeviceId!.Value, result.FirstAdmittedAt!.Value,
                    upload.ScanId, device.Id, upload.ScannedAt.ToUniversalTime()));
            }
        }

        return results;
    }

    private async Task<ScanResult> ResultOfAsync(Scan scan, CancellationToken cancellationToken)
    {
        if (scan.Outcome != ScanOutcome.Duplicate)
        {
            return new ScanResult(scan.Id, scan.Outcome.ToString());
        }

        var first = await doorList.FirstAdmissionAsync(scan.TicketId, cancellationToken);
        return new ScanResult(scan.Id, scan.Outcome.ToString(), first?.DeviceId, first?.At);
    }
}
