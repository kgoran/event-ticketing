using EventTicketing.Tooling.Clients.Public;
using EventTicketing.Tooling.ScalingLab;
using EventTicketing.Tooling.ScalingLab.Components;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Bundle;

// The Scaling Lab (ADR-0017): http://localhost:5300 when the AppHost runs. A slider sets the load
// generator's rate; the page shows, second by second, what was offered and what the system did.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Instances>();

builder.Services.AddHttpClient<LoadGeneratorApi>(http =>
{
    http.BaseAddress = Url("LOADGENERATOR_URL", "http://localhost:5301");
    http.Timeout = TimeSpan.FromSeconds(3);
});

// The database panel (SL3): CloudNativePG through the Kubernetes API, lag and connections from Prometheus.
builder.Services.AddHttpClient("prometheus", http =>
{
    http.BaseAddress = Url("PROMETHEUS_URL", "http://localhost:9090");
    http.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddSingleton(services => new DatabaseView(
    services.GetRequiredService<IConfiguration>(),
    services.GetRequiredService<IHttpClientFactory>().CreateClient("prometheus")));

// The lab cluster's Toxiproxy (D57): the slow database scenario.
builder.Services.AddHttpClient<DatabaseDelay>(http =>
{
    http.BaseAddress = Url("TOXIPROXY_URL", "http://localhost:8474");
    http.Timeout = TimeSpan.FromSeconds(3);
});

// Anonymous reads only: the event list for the picker, and free places of the event under load.
builder.Services.AddSingleton(_ => new PublicApiClient(new DefaultRequestAdapter(new AnonymousAuthenticationProvider())
{
    BaseUrl = Url("PUBLICAPI_URL", "http://localhost:5101").ToString().TrimEnd('/'),
}));

var app = builder.Build();

app.UseAntiforgery();
app.MapHealthChecks("/health");
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// The database's added delay, for the page's scenario and for anyone with a terminal or a test:
// curl -X PUT localhost:5300/database/delay -H "Content-Type: application/json" -d '{"milliseconds":50}'
var delay = app.MapGroup("/database/delay").AddEndpointFilter(async (context, next) =>
    DatabaseDelay.Available ? await next(context) : Results.NotFound("No Toxiproxy here: the database can be slowed in the lab cluster only."));
delay.MapGet("", async (DatabaseDelay database, CancellationToken cancellationToken) =>
    Results.Ok(new DelaySetting(await database.GetAsync(cancellationToken))));
delay.MapPut("", async (DelaySetting setting, DatabaseDelay database, CancellationToken cancellationToken) =>
{
    if (setting.Milliseconds is < 0 or > DatabaseDelay.MaxMilliseconds)
    {
        return Results.BadRequest($"Between 0 and {DatabaseDelay.MaxMilliseconds} ms.");
    }

    await database.SetAsync(setting.Milliseconds, cancellationToken);
    return Results.Ok(setting);
});

app.Run();

static Uri Url(string variable, string fallback) =>
    new((Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : fallback).TrimEnd('/') + "/");

/// <summary>Internal, unlike .NET's default: the lab's tests also reference the load generator, whose Program is public.</summary>
internal partial class Program;

/// <summary>The database's added delay, in milliseconds (D57).</summary>
internal sealed record DelaySetting(int Milliseconds);
