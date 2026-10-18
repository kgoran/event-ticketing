using System.Diagnostics.Metrics;

namespace EventTicketing.Inventory.Application;

/// <summary>
/// Inventory's metrics (docs/observability.md). ServiceDefaults subscribes to every "EventTicketing.*"
/// meter. Hold results come from the command metric (PlaceHold, by error code).
/// </summary>
public static class InventoryTelemetry
{
    public const string Name = "EventTicketing.Inventory";

    private static readonly Meter Meter = new(Name);

    internal static readonly Counter<long> PlacesHeld = Meter.CreateCounter<long>(
        "eventticketing.inventory.places_held",
        description: "Places (seats and standing slots) claimed by successful holds.");

    internal static readonly Counter<long> Swept = Meter.CreateCounter<long>(
        "eventticketing.inventory.swept",
        description: "Rows the sweeper tidied up, by kind (seats shown available again, holds marked expired).");
}
