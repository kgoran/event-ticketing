using EventTicketing.Shared.Application.Idempotency;
using EventTicketing.Shared.IntegrationTests.Sales;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Shared.IntegrationTests;

public sealed class IdempotencyStoreTests(PostgresFixture postgres)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_key_is_claimed_once_then_replays_its_stored_result()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());

        await app.InScopeAsync(async services =>
        {
            var store = services.GetRequiredService<IIdempotencyStore>();

            (await store.TryClaimAsync("key-1", typeof(PlaceOrder), CancellationToken)).Should().BeOfType<IdempotencyClaim.Claimed>();
            (await store.TryClaimAsync("key-1", typeof(PlaceOrder), CancellationToken)).Should().BeOfType<IdempotencyClaim.InProgress>();

            await store.CompleteAsync("key-1", typeof(PlaceOrder), new StoredResult(true, "\"first\"", null, null, null), CancellationToken);

            (await store.TryClaimAsync("key-1", typeof(PlaceOrder), CancellationToken))
                .Should().BeOfType<IdempotencyClaim.AlreadyCompleted>()
                .Which.Result.ValueJson.Should().Be("\"first\"");
            return true;
        });
    }

    [Fact]
    public async Task An_abandoned_claim_can_be_taken_over_once_its_lease_expires()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());

        await app.InScopeAsync(async services =>
        {
            var store = services.GetRequiredService<IIdempotencyStore>();
            await store.TryClaimAsync("key-1", typeof(PlaceOrder), CancellationToken);

            await app.ExecuteAsync("UPDATE sales.idempotency_records SET locked_until = now() - interval '1 second'");

            (await store.TryClaimAsync("key-1", typeof(PlaceOrder), CancellationToken)).Should().BeOfType<IdempotencyClaim.Claimed>();
            return true;
        });
    }

    [Fact]
    public async Task A_released_key_can_be_claimed_again()
    {
        await using var app = await TestApp.CreateAsync(await postgres.CreateDatabaseAsync());

        await app.InScopeAsync(async services =>
        {
            var store = services.GetRequiredService<IIdempotencyStore>();
            await store.TryClaimAsync("key-1", typeof(PlaceOrder), CancellationToken);
            await store.ReleaseAsync("key-1", typeof(PlaceOrder), CancellationToken);

            (await store.TryClaimAsync("key-1", typeof(PlaceOrder), CancellationToken)).Should().BeOfType<IdempotencyClaim.Claimed>();
            return true;
        });
    }
}
