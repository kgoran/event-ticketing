using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Shared.Infrastructure.Scheduling;
using EventTicketing.Shared.IntegrationTests.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Shared.IntegrationTests;

public sealed class SchedulerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_scheduled_message_runs_when_it_is_due_and_not_before()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());
        await app.SendAsync<ScheduleReminder, Unit>(new ScheduleReminder("Call Ana", DateTimeOffset.UtcNow.AddHours(1)));

        (await app.SchedulerOnceAsync()).Should().Be(0, "it is not due yet");
        (await app.CountAsync("sales.reminders")).Should().Be(0);

        await app.ExecuteAsync("UPDATE sales.scheduled_messages SET due_at = now() - interval '1 second'");
        (await app.SchedulerOnceAsync()).Should().Be(1);

        (await app.CountAsync("sales.reminders", "text = 'Call Ana'")).Should().Be(1);
        (await app.CountAsync("sales.scheduled_messages")).Should().Be(0, "the row is deleted in the handler's transaction");
    }

    [Fact]
    public async Task A_recurring_message_runs_and_schedules_its_next_occurrence()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var app = await TestApp.CreateAsync(database, services: Every5Minutes);

        // The first pass starts the job (due at once) and runs it.
        (await app.SchedulerOnceAsync()).Should().Be(1);
        (await app.CountAsync("sales.reminders", "text = 'tick'")).Should().Be(1);
        (await app.CountAsync("sales.scheduled_messages")).Should().Be(1, "the next occurrence is pending");
        (await app.ScalarAsync<bool>("SELECT bool_and(due_at > now() + interval '4 minutes') FROM sales.scheduled_messages")).Should().BeTrue();

        // Another Workers instance starting finds it pending and adds nothing.
        await using var second = await TestApp.CreateAsync(database, createTables: false, services: Every5Minutes);
        (await second.SchedulerOnceAsync()).Should().Be(0);
        (await app.CountAsync("sales.scheduled_messages")).Should().Be(1);

        await app.ExecuteAsync("UPDATE sales.scheduled_messages SET due_at = now() - interval '1 second'");
        (await second.SchedulerOnceAsync()).Should().Be(1);
        (await app.CountAsync("sales.reminders", "text = 'tick'")).Should().Be(2);
        (await app.CountAsync("sales.scheduled_messages")).Should().Be(1);
    }

    [Fact]
    public async Task Instances_checking_at_the_same_moment_start_a_recurring_message_once()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());
        var go = new TaskCompletionSource();

        // Twenty instances, connections already open, released together.
        var checks = Enumerable.Range(0, 20).Select(_ => app.InScopeAsync(async provider =>
        {
            var module = provider.GetRequiredService<ModuleRegistry>().BySchema("sales");
            var context = (ModuleDbContext)provider.GetRequiredService(module.ContextType);
            await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await go.Task;
            await RecurringMessageSql.EnsurePendingAsync(context, module, typeof(Tick).FullName!, TestContext.Current.CancellationToken);
            return true;
        })).ToList();

        go.SetResult();
        await Task.WhenAll(checks);

        (await app.CountAsync("sales.scheduled_messages")).Should().Be(1);
    }

    private static void Every5Minutes(IServiceCollection services) => services.AddRecurringMessage<Tick>(TimeSpan.FromMinutes(5));
}
