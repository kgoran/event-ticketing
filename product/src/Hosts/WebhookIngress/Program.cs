using EventTicketing.Catalog;
using EventTicketing.Inventory.Infrastructure;
using EventTicketing.Orders.Infrastructure;
using EventTicketing.Payments.Infrastructure;
using EventTicketing.Shared.Infrastructure.Modules;
using Scalar.AspNetCore;

// Payment provider callbacks: verify, store in the inbox, reply 200.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddModules(HostKind.WebhookIngress, new CatalogModule(), new InventoryModule(), new OrdersModule(), new PaymentsModule());
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

app.MapModuleEndpoints();

app.Run();
