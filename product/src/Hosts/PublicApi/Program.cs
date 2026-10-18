using EventTicketing.Shared.Infrastructure.Modules;
using Scalar.AspNetCore;

// Fans: browse events, hold seats, check out, my tickets.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddModules(HostKind.PublicApi); // modules are added here as they are built
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
