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

builder.Services.AddHttpClient<LoadGeneratorApi>(http =>
{
    http.BaseAddress = Url("LOADGENERATOR_URL", "http://localhost:5301");
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

app.Run();

static Uri Url(string variable, string fallback) =>
    new((Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : fallback).TrimEnd('/') + "/");

/// <summary>Internal, unlike .NET's default: the lab's tests also reference the load generator, whose Program is public.</summary>
internal partial class Program;
