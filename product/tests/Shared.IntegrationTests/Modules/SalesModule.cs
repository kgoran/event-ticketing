using EventTicketing.Shared.Application;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Application.Scheduling;
using EventTicketing.Shared.Contracts;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Shared.IntegrationTests.Sales;

// A tiny test module: places orders (publishing OrderPlaced) and schedules reminders.

internal sealed class SalesModule : IModule
{
    public string Name => "Sales";

    public void Register(IHostApplicationBuilder builder, HostKind host)
    {
        builder.AddModuleDatabase<SalesDbContext>(host, "sales", typeof(SalesModule).Namespace);

        // Both test modules live in this one assembly, so Sales registers the handlers of both.
        builder.Services.AddCommandHandlers(typeof(SalesModule).Assembly);
        builder.Services.AddMessageHandlers(typeof(SalesModule).Assembly);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
    }
}

internal sealed class SalesDbContext(DbContextOptions<SalesDbContext> options) : ModuleDbContext(options)
{
    public DbSet<SaleOrder> Orders => Set<SaleOrder>();

    public DbSet<Reminder> Reminders => Set<Reminder>();

    protected override string Schema => "sales";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<SaleOrder>().ToTable("orders");
        modelBuilder.Entity<Reminder>().ToTable("reminders");
    }
}

internal sealed class SaleOrder
{
    public Guid Id { get; init; }

    public required string Customer { get; init; }
}

internal sealed class Reminder
{
    public Guid Id { get; init; }

    public required string Text { get; init; }
}

[IntegrationEvent("sales.order-placed", 1)]
internal sealed record OrderPlaced(Guid MessageId, DateTimeOffset OccurredAt, Guid OrderId, string Customer) : IIntegrationEvent;

internal sealed record PlaceOrder(string Customer, bool RejectAfterPublishing = false) : ICommand<Guid>;

internal sealed class PlaceOrderHandler(SalesDbContext db, IIntegrationEventPublisher events, TimeProvider time)
    : ICommandHandler<PlaceOrder, Guid>
{
    public Task<Result<Guid>> HandleAsync(PlaceOrder command, CancellationToken cancellationToken)
    {
        var order = new SaleOrder { Id = Guid.CreateVersion7(), Customer = command.Customer };
        db.Orders.Add(order);
        events.Publish(new OrderPlaced(Guid.CreateVersion7(), time.GetUtcNow(), order.Id, order.Customer));

        Result<Guid> result = command.RejectAfterPublishing
            ? Error.Conflict("sales.rejected", "Rejected after the event was published.")
            : order.Id;

        return Task.FromResult(result);
    }
}

internal sealed record ScheduleReminder(string Text, DateTimeOffset DueAt) : ICommand<Unit>;

internal sealed class ScheduleReminderHandler(IMessageScheduler scheduler) : ICommandHandler<ScheduleReminder, Unit>
{
    public Task<Result<Unit>> HandleAsync(ScheduleReminder command, CancellationToken cancellationToken)
    {
        scheduler.Schedule(new ReminderDue(command.Text), command.DueAt);
        return Task.FromResult<Result<Unit>>(Unit.Value);
    }
}

internal sealed record ReminderDue(string Text);

/// <summary>A message with no data, for recurring-message tests: each run adds a "tick" reminder.</summary>
internal sealed record Tick;

internal sealed class TickHandler(SalesDbContext db) : IScheduledMessageHandler<Tick>
{
    public Task HandleAsync(Tick message, CancellationToken cancellationToken)
    {
        db.Reminders.Add(new Reminder { Id = Guid.CreateVersion7(), Text = "tick" });
        return Task.CompletedTask;
    }
}

internal sealed class ReminderDueHandler(SalesDbContext db) : IScheduledMessageHandler<ReminderDue>
{
    public Task HandleAsync(ReminderDue message, CancellationToken cancellationToken)
    {
        db.Reminders.Add(new Reminder { Id = Guid.CreateVersion7(), Text = message.Text });
        return Task.CompletedTask;
    }
}

internal sealed class PlaceOrderAuthorizer : ICommandAuthorizer<PlaceOrder>
{
    public ValueTask<bool> IsAllowedAsync(PlaceOrder command, IExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}

internal sealed class ScheduleReminderAuthorizer : ICommandAuthorizer<ScheduleReminder>
{
    public ValueTask<bool> IsAllowedAsync(ScheduleReminder command, IExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
