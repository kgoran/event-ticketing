using EventTicketing.Shared.Infrastructure.Authentication;
using EventTicketing.Shared.Infrastructure.Modules;
using Scalar.AspNetCore;

// Organizers and admins: events, organizers, reporting, refunds.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddApiAuthentication();
builder.AddModules(HostKind.BackOfficeApi); // modules are added here as they are built
builder.Services.AddOpenApi();

var app = builder.Build();

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
