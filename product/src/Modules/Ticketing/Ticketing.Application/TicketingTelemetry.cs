using System.Diagnostics.Metrics;

namespace EventTicketing.Ticketing.Application;

/// <summary>Ticketing's metrics (docs/observability.md). ServiceDefaults subscribes to every "EventTicketing.*" meter.</summary>
public static class TicketingTelemetry
{
    public const string Name = "EventTicketing.Ticketing";

    private static readonly Meter Meter = new(Name);

    internal static readonly Counter<long> TicketsIssued = Meter.CreateCounter<long>(
        "eventticketing.tickets.issued",
        description: "Tickets issued for confirmed orders, one per place.");

    internal static readonly Counter<long> TicketsRevoked = Meter.CreateCounter<long>(
        "eventticketing.tickets.revoked",
        description: "Tickets revoked: they no longer let anyone in (D63).");
}
