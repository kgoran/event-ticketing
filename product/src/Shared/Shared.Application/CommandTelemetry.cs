using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EventTicketing.Shared.Application;

/// <summary>Tracing and metrics for commands. ServiceDefaults subscribes to every "EventTicketing.*" source and meter.</summary>
public static class CommandTelemetry
{
    public const string Name = "EventTicketing.Commands";

    internal static readonly ActivitySource ActivitySource = new(Name);

    private static readonly Meter Meter = new(Name);

    internal static readonly Counter<long> Handled = Meter.CreateCounter<long>(
        "eventticketing.commands.handled",
        description: "Commands handled, by command and outcome.");

    internal static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "eventticketing.commands.duration",
        unit: "s",
        description: "Time spent in command handlers.");
}
