using System.Security.Claims;
using EventTicketing.CheckIn.Application.Scanning;
using EventTicketing.CheckIn.Domain;
using EventTicketing.CheckIn.Infrastructure.Persistence;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Http;
using EventTicketing.Ticketing.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.CheckIn.Infrastructure.Endpoints;

/// <summary>
/// An event's door list, or the changes since <paramref name="Cursor"/> (ADR-0013): tickets that
/// let their holders in, tickets revoked, and tickets already admitted at another door. No personal
/// data. With <paramref name="More"/>, ask again from the new cursor for the rest.
/// </summary>
/// <param name="Keys">The public keys that verify the tickets' codes, offline.</param>
public sealed record DoorListPage(
    Guid EventId,
    long Cursor,
    bool More,
    TicketKeySet? Keys,
    IReadOnlyList<Guid> Issued,
    IReadOnlyList<Guid> Revoked,
    IReadOnlyList<Admission> Admitted);

/// <summary>A ticket let in: when, and by which device.</summary>
public sealed record Admission(Guid TicketId, Guid DeviceId, DateTimeOffset At);

/// <summary>A batch of scans from a device.</summary>
public sealed record ScanBatch(IReadOnlyList<ScanUpload> Scans);

/// <summary>The device's identity, from the fixed claims its Keycloak client puts in every token (D61).</summary>
internal static class DeviceClaims
{
    public const string EventId = "event_id";
    public const string DeviceId = "device_id";

    public static (Guid EventId, Guid DeviceId)? Of(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(EventId)?.Value, out var eventId) && Guid.TryParse(user.FindFirst(DeviceId)?.Value, out var deviceId)
            ? (eventId, deviceId)
            : null;
}

/// <summary>CheckInApi (ADR-0013, ADR-0056): what the devices at the doors call.</summary>
internal static class DoorEndpoints
{
    public const int MaxPage = 20_000;

    public static void MapForDevices(IEndpointRouteBuilder endpoints)
    {
        var door = endpoints.MapGroup("/events/{eventId:guid}").WithTags("Door");

        door.MapGet("/door-list", async (Guid eventId, long? since, int? limit, ClaimsPrincipal user, IExecutionContext caller, CheckInDbContext db, ITicketingApi ticketing, HttpResponse response, CancellationToken cancellationToken) =>
            {
                if (await RefusedAsync(eventId, user, caller, db, cancellationToken) is { } refused)
                {
                    return refused.ToProblem();
                }

                var after = Math.Max(0, since ?? 0);
                var size = Math.Clamp(limit ?? MaxPage, 1, MaxPage);
                var entries = await db.DoorList.AsNoTracking()
                    .Where(entry => entry.EventId == eventId && entry.Sequence > after)
                    .OrderBy(entry => entry.Sequence)
                    .Take(size + 1)
                    .ToListAsync(cancellationToken);
                var page = entries.Take(size).ToList();

                response.Headers.CacheControl = "no-store";
                return Results.Ok(new DoorListPage(
                    eventId,
                    page.Count > 0 ? page[^1].Sequence : after,
                    entries.Count > size,
                    await ticketing.KeysAsync(eventId, cancellationToken),
                    [.. page.Where(entry => entry.Change == DoorListChange.Issued).Select(entry => entry.TicketId)],
                    [.. page.Where(entry => entry.Change == DoorListChange.Revoked).Select(entry => entry.TicketId)],
                    [.. page.Where(entry => entry.Change == DoorListChange.Admitted).Select(entry => new Admission(entry.TicketId, entry.DeviceId!.Value, entry.At))]));
            })
            .WithName("GetDoorList")
            .WithSummary("The event's door list, or what changed since a cursor: tickets, revocations, admissions elsewhere, and the keys")
            .Produces<DoorListPage>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        door.MapPost("/scans", async (Guid eventId, ScanBatch batch, ClaimsPrincipal user, ICommandHandler<UploadScans, IReadOnlyList<ScanResult>> handler, CancellationToken cancellationToken) =>
            {
                if (DeviceClaims.Of(user) is not { } device || device.EventId != eventId)
                {
                    return WrongDevice.ToProblem();
                }

                var result = await handler.HandleAsync(new UploadScans(eventId, device.DeviceId, batch.Scans ?? []), cancellationToken);
                return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem();
            })
            .WithName("UploadScans")
            .WithSummary("Uploads the device's scans: safe to repeat; the first admission of a ticket counts, a later one is a duplicate")
            .Produces<IReadOnlyList<ScanResult>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static readonly Error WrongDevice = Error.Forbidden("device.wrong_event", "Only a device registered for this event may call this.");

    /// <summary>Why the caller may not read the door list; null if it may: an active device of this event.</summary>
    private static async Task<Error?> RefusedAsync(Guid eventId, ClaimsPrincipal user, IExecutionContext caller, CheckInDbContext db, CancellationToken cancellationToken)
    {
        if (!caller.Roles.Contains(Roles.DoorStaff) || DeviceClaims.Of(user) is not { } device || device.EventId != eventId)
        {
            return WrongDevice;
        }

        var active = await db.Devices.AsNoTracking()
            .AnyAsync(d => d.Id == device.DeviceId && d.EventId == eventId && d.RevokedAt == null, cancellationToken);
        return active ? null : Error.Forbidden("device.not_active", "This device is revoked, or isn't registered for this event.");
    }
}
