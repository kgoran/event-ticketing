using EventTicketing.Catalog;
using EventTicketing.Inventory.Infrastructure;
using EventTicketing.Orders.Infrastructure;
using EventTicketing.Payments.Infrastructure;
using EventTicketing.Shared.Infrastructure.Authentication;
using EventTicketing.Shared.Infrastructure.Modules;
using Scalar.AspNetCore;

// Organizers and admins: events, organizers, reporting, refunds.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddApiAuthentication();
builder.AddModules(HostKind.BackOfficeApi, new CatalogModule(), new InventoryModule(), new OrdersModule(), new PaymentsModule());
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

app.MapCallerEndpoint();
app.MapModuleEndpoints();

app.Run();
