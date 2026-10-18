using EventTicketing.Shared.Domain;
using EventTicketing.Shared.IntegrationTests.Sales;

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
}
