namespace EventTicketing.Shared.IntegrationTests;

/// <summary>One transaction per command: changes, outbox messages and the audit entry commit together or not at all.</summary>
public sealed class TransactionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_command_commits_its_changes_its_event_and_its_audit_entry_together()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());

        var result = await app.PlaceOrderAsync("Ana");

        result.IsSuccess.Should().BeTrue();
        (await app.CountAsync("sales.orders")).Should().Be(1);
        (await app.CountAsync("sales.outbox_messages", "type = 'sales.order-placed'")).Should().Be(1);
        (await app.CountAsync("sales.audit_records", "command = 'PlaceOrder'")).Should().Be(1);
    }

    [Fact]
    public async Task A_failed_command_commits_nothing_not_even_the_event_it_published()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());

        var result = await app.PlaceOrderAsync("Ana", reject: true);

        result.Error.Code.Should().Be("sales.rejected");
        (await app.CountAsync("sales.orders")).Should().Be(0);
        (await app.CountAsync("sales.outbox_messages")).Should().Be(0);
        (await app.CountAsync("sales.audit_records")).Should().Be(0);
    }

    [Fact]
    public async Task A_repeated_idempotency_key_returns_the_first_result_without_running_again()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());

        var first = await app.PlaceOrderAsync("Ana", idempotencyKey: "key-1");
        var second = await app.PlaceOrderAsync("Ana", idempotencyKey: "key-1");

        second.Value.Should().Be(first.Value);
        (await app.CountAsync("sales.orders")).Should().Be(1);
        (await app.CountAsync("sales.outbox_messages")).Should().Be(1);
    }
}
