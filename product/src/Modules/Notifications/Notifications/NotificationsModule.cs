using EventTicketing.Notifications.Email;
using EventTicketing.Notifications.Persistence;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Notifications;

/// <summary>
/// Notifications (ADR-0006, ADR-0052): emails the fan their tickets and tells them about refunds.
/// Everything it needs arrives in the events it consumes (ADR-0007); it runs in Workers and has no
/// endpoints.
/// </summary>
public sealed class NotificationsModule : IModule
{
    public string Name => "Notifications";

    public void Register(IHostApplicationBuilder builder, HostKind host)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModuleDatabase<NotificationsDbContext>(host, NotificationsDbContext.SchemaName);

        var module = typeof(NotificationsModule).Assembly;
        builder.Services.AddCommandHandlers(module);
        builder.Services.AddMessageHandlers(module);

        builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
        builder.Services.AddScoped<EmailComposer>();
        builder.Services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
    }
}
