using EventTicketing.Catalog;
using EventTicketing.Inventory.Infrastructure;
using EventTicketing.Notifications;
using EventTicketing.Orders.Infrastructure;
using EventTicketing.Organizers;
using EventTicketing.Payments.Infrastructure;
using EventTicketing.PublicApi;
using EventTicketing.Shared.Infrastructure.Authentication;
using EventTicketing.Shared.Infrastructure.Caching;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Ticketing.Infrastructure;
using Scalar.AspNetCore;

// Fans: browse events, hold seats, check out, my tickets.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddWarmUp(PublicApiWarmUp.RunAsync);
builder.AddApiAuthentication();
builder.AddSharedOutputCache();
builder.AddModules(HostKind.PublicApi, new CatalogModule(), new InventoryModule(), new OrdersModule(), new PaymentsModule(), new OrganizersModule(), new TicketingModule(), new NotificationsModule());
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();

// Every endpoint needs a signed-in caller unless it says otherwise (ADR-0027).
app.MapDefaultEndpoints();
app.MapOpenApi().AllowAnonymous();
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference().AllowAnonymous();
}

app.MapCallerEndpoint();
app.MapModuleEndpoints();

app.Run();
