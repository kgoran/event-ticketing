using EventTicketing.Shared.Infrastructure.Modules;
using Scalar.AspNetCore;

// Organizers and admins: events, organizers, reporting, refunds.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddModules(HostKind.BackOfficeApi); // modules are added here as they are built
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

app.MapModuleEndpoints();

app.Run();
