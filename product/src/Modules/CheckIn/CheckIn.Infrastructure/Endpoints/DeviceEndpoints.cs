using EventTicketing.CheckIn.Application;
using EventTicketing.CheckIn.Application.Devices;
using EventTicketing.CheckIn.Infrastructure.Persistence;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.CheckIn.Infrastructure.Endpoints;

/// <summary>A new device's name, e.g. "North gate 1".</summary>
public sealed record RegisterDeviceRequest(string Name);

/// <summary>A device as the back office lists it; never its secret.</summary>
/// <param name="LastUploadAt">When it last uploaded scans: a device that has gone quiet may be offline.</param>
public sealed record DeviceSummary(Guid DeviceId, string Name, string ClientId, DateTimeOffset RegisteredAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUploadAt, int Scans);

/// <summary>BackOfficeApi: an event's devices, for an admin or the event's organizer (D60, D61).</summary>
internal static class DeviceEndpoints
{
    public static void MapForBackOffice(IEndpointRouteBuilder endpoints)
    {
        var devices = endpoints.MapGroup("/events/{eventId:guid}/devices").WithTags("Devices");

        devices.MapPost("/", async (Guid eventId, RegisterDeviceRequest request, ICommandHandler<RegisterDevice, RegisteredDevice> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new RegisterDevice(eventId, request.Name), cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/events/{eventId}/devices/{result.Value.DeviceId}", result.Value)
                    : result.Error.ToProblem();
            })
            .WithName("RegisterDevice")
            .WithSummary("Registers a device for the event's doors, with a Keycloak client: its secret is shown this once")
            .Produces<RegisteredDevice>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        devices.MapGet("/", async (Guid eventId, IExecutionContext caller, ICheckInEvents events, CheckInDbContext db, CancellationToken cancellationToken) =>
            {
                if (!await EventStaff.MayManageAsync(eventId, caller, events, cancellationToken))
                {
                    return Error.Forbidden("device.forbidden", "Only an admin or the event's organizer sees its devices.").ToProblem();
                }

                var list = await db.Devices.AsNoTracking()
                    .Where(device => device.EventId == eventId)
                    .OrderBy(device => device.RegisteredAt)
                    .Select(device => new DeviceSummary(
                        device.Id,
                        device.Name,
                        device.ClientId,
                        device.RegisteredAt,
                        device.RevokedAt,
                        db.Scans.Where(scan => scan.DeviceId == device.Id).Max(scan => (DateTimeOffset?)scan.ReceivedAt),
                        db.Scans.Count(scan => scan.DeviceId == device.Id)))
                    .ToListAsync(cancellationToken);
                return Results.Ok(list);
            })
            .WithName("GetDevices")
            .WithSummary("The event's devices, revoked ones too, with when each last uploaded scans")
            .Produces<IReadOnlyList<DeviceSummary>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        devices.MapDelete("/{deviceId:guid}", async (Guid eventId, Guid deviceId, ICommandHandler<RevokeDevice, Unit> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new RevokeDevice(eventId, deviceId), cancellationToken);
                return result.IsSuccess ? Results.NoContent() : result.Error.ToProblem();
            })
            .WithName("RevokeDevice")
            .WithSummary("Revokes a device: CheckIn refuses it at once, and its Keycloak client is deleted")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
