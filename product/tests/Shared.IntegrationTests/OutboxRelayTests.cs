namespace EventTicketing.Shared.IntegrationTests;

/// <summary>
/// M1a's definition of done for F4: duplicate delivery is dropped, and a crash between commit and
/// publish loses nothing. Plus retries, dead-lettering, leases and parallel relays.
/// </summary>
public sealed class OutboxRelayTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Delivers_an_event_to_every_consumer_and_marks_it_processed()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());
        await app.PlaceOrderAsync("Ana");

        (await app.RelayOnceAsync()).Should().Be(1);

        (await app.CountAsync("warehouse.shipments")).Should().Be(1);
        (await app.CountAsync("warehouse.labels")).Should().Be(1);
        (await app.CountAsync("sales.outbox_messages", "processed_at IS NOT NULL")).Should().Be(1);
        (await app.RelayOnceAsync()).Should().Be(0, "nothing is left to deliver");
    }

    [Fact]
    public async Task An_event_committed_just_before_a_crash_is_still_delivered_after_restart()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using (var beforeCrash = await TestApp.CreateAsync(database))
        {
            await beforeCrash.PlaceOrderAsync("Ana"); // committed, but the process "dies" before relaying
        }

        await using var afterRestart = await TestApp.CreateAsync(database, createTables: false);
        await afterRestart.RelayOnceAsync();

        (await afterRestart.CountAsync("warehouse.shipments")).Should().Be(1);
    }

    [Fact]
    public async Task A_redelivered_event_is_dropped_by_the_inbox()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());
        await app.PlaceOrderAsync("Ana");
        await app.RelayOnceAsync();

        // The relay delivered, then "crashed" before marking the message processed.
        await app.ExecuteAsync("UPDATE sales.outbox_messages SET processed_at = NULL, locked_until = NULL");
        await app.RelayOnceAsync();

        (await app.CountAsync("warehouse.shipments")).Should().Be(1);
        (await app.CountAsync("warehouse.labels")).Should().Be(1);
        (await app.CountAsync("warehouse.inbox_messages")).Should().Be(2, "one row per consumer, not per delivery");
    }

    [Fact]
    public async Task When_one_consumer_fails_the_retry_does_not_repeat_the_consumer_that_succeeded()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());
        app.Faults.FailLabelPrinting(times: 1);
        await app.PlaceOrderAsync("Ana");

        await app.RelayOnceAsync();
        (await app.CountAsync("warehouse.shipments")).Should().Be(1);
        (await app.CountAsync("warehouse.labels")).Should().Be(0);
        (await app.CountAsync("sales.outbox_messages", "processed_at IS NULL AND attempts = 1 AND last_error LIKE '%label printer%'")).Should().Be(1);

        await app.ExecuteAsync("UPDATE sales.outbox_messages SET next_attempt_at = now()"); // skip the backoff wait
        await app.RelayOnceAsync();

        (await app.CountAsync("warehouse.shipments")).Should().Be(1, "the shipping consumer already handled it");
        (await app.CountAsync("warehouse.labels")).Should().Be(1);
        (await app.CountAsync("sales.outbox_messages", "processed_at IS NOT NULL")).Should().Be(1);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_dead_lettered_after_the_last_attempt()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync(), options => options.MaxAttempts = 2);
        app.Faults.FailShipping(times: int.MaxValue);
        await app.PlaceOrderAsync("Ana");

        await app.RelayOnceAsync();
        await app.ExecuteAsync("UPDATE sales.outbox_messages SET next_attempt_at = now()");
        await app.RelayOnceAsync();

        (await app.CountAsync("sales.outbox_messages", "dead_lettered_at IS NOT NULL AND attempts = 2")).Should().Be(1);
        (await app.RelayOnceAsync()).Should().Be(0, "a dead-lettered message is not retried");
    }

    [Fact]
    public async Task A_claimed_message_is_left_alone_until_its_lease_expires()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());
        await app.PlaceOrderAsync("Ana");

        // Another relay claimed it and has not finished (or died while holding the lease).
        await app.ExecuteAsync("UPDATE sales.outbox_messages SET locked_until = now() + interval '1 hour'");
        (await app.RelayOnceAsync()).Should().Be(0);

        await app.ExecuteAsync("UPDATE sales.outbox_messages SET locked_until = now() - interval '1 second'");
        (await app.RelayOnceAsync()).Should().Be(1);
        (await app.CountAsync("warehouse.shipments")).Should().Be(1);
    }

    [Fact]
    public async Task Two_relays_running_in_parallel_deliver_every_event_exactly_once()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync(), options => options.BatchSize = 3);
        for (var i = 0; i < 20; i++)
        {
            await app.PlaceOrderAsync($"customer {i}");
        }

        var claimed = await Task.WhenAll(app.RelayUntilEmptyAsync(), app.RelayUntilEmptyAsync());

        claimed.Sum().Should().Be(20, "SKIP LOCKED gives each message to exactly one relay");
        (await app.CountAsync("warehouse.shipments")).Should().Be(20);
        (await app.CountAsync("warehouse.labels")).Should().Be(20);
    }
}
