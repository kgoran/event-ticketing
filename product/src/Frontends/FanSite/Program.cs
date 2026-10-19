using EventTicketing.FanSite.Api;
using EventTicketing.FanSite.Authentication;
using EventTicketing.FanSite.Components;
using EventTicketing.FanSite.Holds;

// The fan site (ADR-0049): browse events, pick seats, hold them, check out and follow the order.
// It uses the product only through PublicApi's HTTP API.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddFanAuthentication();
builder.AddPublicApiClient();
builder.Services.AddScoped<MyHolds>();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapStaticAssets();
app.MapAccountEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
