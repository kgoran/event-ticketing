using System.Buffers.Text;
using System.Security.Cryptography;
using EventTicketing.CheckIn.Domain;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using FluentValidation;

namespace EventTicketing.CheckIn.Application.Devices;

/// <summary>Registers a device for an event's doors (D61). Also the request body is <c>{ "name": … }</c>.</summary>
public sealed record RegisterDevice(Guid EventId, string Name) : ICommand<RegisteredDevice>;

/// <summary>The new device, with what it needs to sign in: shown once.</summary>
/// <param name="ClientSecret">The device's secret; it isn't stored, so it can't be shown again.</param>
public sealed record RegisteredDevice(Guid DeviceId, Guid EventId, string Name, string ClientId, string ClientSecret, Uri? TokenEndpoint);

/// <summary>Revokes a device: it can't sign in again, and CheckIn refuses it at once.</summary>
public sealed record RevokeDevice(Guid EventId, Guid DeviceId) : ICommand<Unit>;

internal sealed class RegisterDeviceValidator : AbstractValidator<RegisterDevice>
{
    public RegisterDeviceValidator()
    {
        RuleFor(command => command.EventId).NotEqual(Guid.Empty);
        RuleFor(command => command.Name).NotEmpty().MaximumLength(100);
    }
}

/// <summary>An event's devices are managed by an admin or by the event's organizer.</summary>
public static class EventStaff
{
    public static async ValueTask<bool> MayManageAsync(Guid eventId, IExecutionContext context, ICheckInEvents events, CancellationToken cancellationToken)
    {
        if (!context.IsAuthenticated)
        {
            return false;
        }

        if (context.Roles.Contains(Roles.Admin))
        {
            return true;
        }

        return context.Roles.Contains(Roles.Organizer)
            && await events.FindAsync(eventId, cancellationToken) is { } checkInEvent
            && context.OrganizerIds.Contains(checkInEvent.OrganizerId);
    }
}

internal sealed class RegisterDeviceAuthorizer(ICheckInEvents events) : ICommandAuthorizer<RegisterDevice>
{
    public ValueTask<bool> IsAllowedAsync(RegisterDevice command, IExecutionContext context, CancellationToken cancellationToken) =>
        EventStaff.MayManageAsync(command.EventId, context, events, cancellationToken);
}

internal sealed class RevokeDeviceAuthorizer(ICheckInEvents events) : ICommandAuthorizer<RevokeDevice>
{
    public ValueTask<bool> IsAllowedAsync(RevokeDevice command, IExecutionContext context, CancellationToken cancellationToken) =>
        EventStaff.MayManageAsync(command.EventId, context, events, cancellationToken);
}

internal sealed class RegisterDeviceHandler(
    ICheckInEvents events,
    IDevices devices,
    IDeviceDirectory directory,
    IExecutionContext caller,
    TimeProvider time)
    : ICommandHandler<RegisterDevice, RegisteredDevice>
{
    public async Task<Result<RegisteredDevice>> HandleAsync(RegisterDevice command, CancellationToken cancellationToken)
    {
        if (await events.FindAsync(command.EventId, cancellationToken) is null)
        {
            return Error.NotFound("device.event_not_found", "CheckIn knows no published event with this ID.");
        }

        if (!directory.Available)
        {
            return new Error("device.directory_unavailable", "No identity provider is configured, so devices can't be given credentials.");
        }

        var device = Device.Register(Guid.CreateVersion7(), command.EventId, command.Name, caller.UserId ?? "unknown", time.GetUtcNow());
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        // The client first: if the database then fails, a client without a device is left, which can
        // only reach an event that refuses it. The other way round, a device could exist without a way in.
        await directory.CreateAsync(new DeviceCredentials(device.Id, device.EventId, device.Name, device.ClientId, secret), cancellationToken);
        devices.Add(device);
        CheckInTelemetry.DeviceChanges.Add(1, new KeyValuePair<string, object?>("eventticketing.device.change", "registered"));
        return new RegisteredDevice(device.Id, device.EventId, device.Name, device.ClientId, secret, directory.TokenEndpoint);
    }
}

internal sealed class RevokeDeviceHandler(IDevices devices, IDeviceDirectory directory, TimeProvider time)
    : ICommandHandler<RevokeDevice, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RevokeDevice command, CancellationToken cancellationToken)
    {
        var device = await devices.FindAsync(command.DeviceId, cancellationToken);
        if (device is null || device.EventId != command.EventId)
        {
            return Error.NotFound("device.not_found", "The event has no device with this ID.");
        }

        var revoked = device.Revoke(time.GetUtcNow());
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        if (directory.Available)
        {
            await directory.DeleteAsync(device.ClientId, cancellationToken);
        }

        CheckInTelemetry.DeviceChanges.Add(1, new KeyValuePair<string, object?>("eventticketing.device.change", "revoked"));
        return Unit.Value;
    }
}
