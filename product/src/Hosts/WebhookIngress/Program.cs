using EventTicketing.Shared.Infrastructure.Modules;
using Scalar.AspNetCore;

// Payment provider callbacks: verify, store in the inbox, reply 200.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddModules(HostKind.WebhookIngress); // modules are added here as they are built
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
