using EventTicketing.Catalog;
using EventTicketing.CheckIn.Infrastructure;
using EventTicketing.Inventory.Infrastructure;
using EventTicketing.Notifications;
using EventTicketing.Orders.Infrastructure;
using EventTicketing.Organizers;
using EventTicketing.Payments.Infrastructure;
using EventTicketing.Shared.Infrastructure.Authentication;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Ticketing.Infrastructure;
using Scalar.AspNetCore;

// The doors (ADR-0013, ADR-0056): registered devices download their event's door list and upload
// their scans, signed in as their own Keycloak clients (D61).
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddApiAuthentication();
builder.AddModules(HostKind.CheckInApi, new CatalogModule(), new InventoryModule(), new OrdersModule(), new PaymentsModule(), new OrganizersModule(), new TicketingModule(), new NotificationsModule(), new CheckInModule());
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

// Every endpoint needs a signed-in caller unless it says otherwise (ADR-0027).
app.MapDefaultEndpoints();
app.MapOpenApi().AllowAnonymous();
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference().AllowAnonymous();
}

app.MapModuleEndpoints();

// The scanner (D62): a Blazor WebAssembly app at /scanner/, which works offline once loaded. Its
// files are public; what it reads and sends needs the device's token.
app.MapStaticAssets().AllowAnonymous();
app.MapFallbackToFile("/scanner/{*path:nonfile}", "scanner/index.html").AllowAnonymous();

app.Run();
