using EventTicketing.Catalog;
using EventTicketing.Inventory.Infrastructure;
using EventTicketing.Orders.Infrastructure;
using EventTicketing.Payments.Infrastructure;
using EventTicketing.Shared.Infrastructure.Modules;

// Background work: event consumers, the outbox relay and the scheduler. Health endpoints only.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddModules(HostKind.Workers, new CatalogModule(), new InventoryModule(), new OrdersModule(), new PaymentsModule());

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
