using System.Diagnostics.Metrics;

namespace EventTicketing.CheckIn.Application;

/// <summary>CheckIn's metrics (docs/observability.md). ServiceDefaults subscribes to every "EventTicketing.*" meter.</summary>
public static class CheckInTelemetry
{
    public const string Name = "EventTicketing.CheckIn";

    private static readonly Meter Meter = new(Name);

    internal static readonly Counter<long> Scans = Meter.CreateCounter<long>(
        "eventticketing.checkin.scans",
        description: "Scans uploaded by the doors, by outcome: admitted, duplicate, refused.");

    internal static readonly Counter<long> DeviceChanges = Meter.CreateCounter<long>(
        "eventticketing.checkin.devices",
        description: "Devices registered and revoked.");
}
