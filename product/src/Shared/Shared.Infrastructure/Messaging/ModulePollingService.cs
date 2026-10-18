using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Shared.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketing.Shared.Infrastructure.Messaging;

/// <summary>
/// Runs a pass over every module database in a loop, in the Workers host. When a pass finds work it
/// goes straight to the next one; when it finds none it waits <see cref="MessagingOptions.PollInterval"/>.
/// </summary>
internal abstract partial class ModulePollingService(
    IServiceScopeFactory scopes,
    ModuleRegistry modules,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger logger)
    : BackgroundService
{
    protected abstract string Name { get; }

    protected abstract Task<int> RunOnceAsync(IServiceProvider services, ModuleDatabase module, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;

            foreach (var module in modules.Modules)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    handled += await RunOnceAsync(scope.ServiceProvider, module, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogPassFailed(logger, exception, Name, module.Schema);
                }
            }

            if (handled == 0)
            {
                try
                {
                    await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The {Service} pass over module {Module} failed")]
    private static partial void LogPassFailed(ILogger logger, Exception exception, string service, string module);
}

internal sealed class OutboxRelayService(
    IServiceScopeFactory scopes,
    ModuleRegistry modules,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelayService> logger)
    : ModulePollingService(scopes, modules, options, timeProvider, logger)
{
    protected override string Name => "outbox relay";

    protected override Task<int> RunOnceAsync(IServiceProvider services, ModuleDatabase module, CancellationToken cancellationToken) =>
        services.GetRequiredService<OutboxRelay>().RunOnceAsync(module, cancellationToken);
}

internal sealed class SchedulerService(
    IServiceScopeFactory scopes,
    ModuleRegistry modules,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<SchedulerService> logger)
    : ModulePollingService(scopes, modules, options, timeProvider, logger)
{
    protected override string Name => "scheduler";

    protected override Task<int> RunOnceAsync(IServiceProvider services, ModuleDatabase module, CancellationToken cancellationToken) =>
        services.GetRequiredService<ScheduledMessageRunner>().RunOnceAsync(module, cancellationToken);
}
