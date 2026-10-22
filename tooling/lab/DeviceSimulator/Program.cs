using System.Globalization;
using EventTicketing.Tooling.DeviceSimulator;

// T8 (ADR-0015, D64): dotnet run --project tooling/lab/DeviceSimulator -- --devices 200 --seconds 90
// Exit code 0 when T8 holds, 1 when it doesn't, 2 when the run couldn't start.
var options = SimulationOptions.Parse(args);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, cancel) =>
{
    cancel.Cancel = true;
    stop.Cancel();
};

try
{
    var report = await new Simulation(options, TimeProvider.System).RunAsync(stop.Token);
    Console.WriteLine();
    Console.WriteLine(report.Describe());
    return report.Holds ? 0 : 1;
}
catch (SimulationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

/// <summary>What a run does, from the command line.</summary>
public sealed record SimulationOptions(
    int Devices,
    TimeSpan Duration,
    TimeSpan SyncInterval,
    double ScansPerSecond,
    bool Revoke,
    string EventName,
    Uri PublicApi,
    Uri BackOfficeApi,
    Uri CheckInApi,
    Uri Keycloak)
{
    public static SimulationOptions Parse(string[] args)
    {
        string Value(string name, string fallback)
        {
            var index = Array.IndexOf(args, "--" + name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        Uri Url(string name, string fallback) => new(Value(name, fallback).TrimEnd('/') + "/");

        return new SimulationOptions(
            int.Parse(Value("devices", "200"), CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(double.Parse(Value("seconds", "90"), CultureInfo.InvariantCulture)),
            TimeSpan.FromSeconds(double.Parse(Value("interval", "5"), CultureInfo.InvariantCulture)),
            double.Parse(Value("scans", "10"), CultureInfo.InvariantCulture),
            !args.Contains("--no-revoke"),
            Value("event", "Demo Concert"),
            Url("publicapi", "http://localhost:5101"),
            Url("backoffice", "http://localhost:5102"),
            Url("checkin", "http://localhost:5104"),
            Url("keycloak", "http://localhost:8180"));
    }
}
