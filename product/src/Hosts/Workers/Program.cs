using EventTicketing.Catalog;
using EventTicketing.CheckIn.Infrastructure;
using EventTicketing.Inventory.Infrastructure;
using EventTicketing.Notifications;
using EventTicketing.Orders.Infrastructure;
using EventTicketing.Organizers;
using EventTicketing.Payments.Infrastructure;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Ticketing.Infrastructure;

// Background work: event consumers, the outbox relay and the scheduler. Health endpoints only.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddModules(HostKind.Workers, new CatalogModule(), new InventoryModule(), new OrdersModule(), new PaymentsModule(), new OrganizersModule(), new TicketingModule(), new NotificationsModule(), new CheckInModule());

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
