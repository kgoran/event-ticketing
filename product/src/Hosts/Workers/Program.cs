using EventTicketing.Shared.Infrastructure.Modules;

// Background work: event consumers, the outbox relay and the scheduler. Health endpoints only.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddModules(HostKind.Workers); // modules are added here as they are built

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
