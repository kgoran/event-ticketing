using EventTicketing.Inventory.Contracts;
using EventTicketing.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1a I2: contract tests for Inventory's module API, called the way Orders calls it. Every call
/// must be safe to repeat (ADR-0009), and the backstop and sweeper must leave consistent state (ADR-0010).
/// </summary>
public sealed class InventoryApiTests(TestSystem system)
{
    private static readonly TimeSpan Backstop = TimeSpan.FromMinutes(35);

    [Fact]
    public async Task BeginCheckout_is_safe_to_repeat_and_moves_the_expiry_to_the_backstop()
    {
        var (sale, fan, holdId) = await HeldAsync(seats: 2);
        var orderId = Guid.NewGuid();

        var first = await ApiAsync(api => api.BeginCheckoutAsync(holdId, orderId, fan, Ct));
        var again = await ApiAsync(api => api.BeginCheckoutAsync(holdId, orderId, fan, Ct));

        first.Outcome.Should().Be(BeginCheckoutOutcome.Started);
        first.EventId.Should().Be(sale.EventId);
        first.Seats.Select(s => s.SeatId).Should().BeEquivalentTo(sale.Seats.Take(2));
        again.Should().BeEquivalentTo(first);

        // Seats and hold agree on the new expiry, by the database clock.
        var seatExpiry = await system.ScalarAsync<DateTime>("SELECT max(held_until) FROM inventory.seats WHERE hold_id = @p0", holdId);
        var holdExpiry = await system.ScalarAsync<DateTime>("SELECT expires_at FROM inventory.holds WHERE id = @p0", holdId);
        holdExpiry.Should().Be(seatExpiry);
        holdExpiry.Should().BeCloseTo(DateTime.UtcNow + Backstop, TimeSpan.FromMinutes(1));

        // Another order, or another fan, can't take it over.
        (await ApiAsync(api => api.BeginCheckoutAsync(holdId, Guid.NewGuid(), fan, Ct))).Outcome.Should().Be(BeginCheckoutOutcome.HoldNotActive);
        (await ApiAsync(api => api.BeginCheckoutAsync(holdId, orderId, "someone-else", Ct))).Outcome.Should().Be(BeginCheckoutOutcome.HoldNotActive);
    }

    [Fact]
    public async Task BeginCheckout_of_an_expired_hold_reports_it_not_active()
    {
        var (_, fan, holdId) = await HeldAsync();
        await system.ExpireHoldAsync(holdId);

        (await ApiAsync(api => api.BeginCheckoutAsync(holdId, Guid.NewGuid(), fan, Ct))).Outcome.Should().Be(BeginCheckoutOutcome.HoldNotActive);
    }

    [Fact]
    public async Task ConfirmHold_is_safe_to_repeat()
    {
        var (sale, fan, holdId) = await HeldAsync(seats: 2);
        var orderId = Guid.NewGuid();
        await ApiAsync(api => api.BeginCheckoutAsync(holdId, orderId, fan, Ct));

        (await ApiAsync(api => api.ConfirmHoldAsync(holdId, orderId, Ct))).Outcome.Should().Be(ConfirmHoldOutcome.Confirmed);
        (await ApiAsync(api => api.ConfirmHoldAsync(holdId, orderId, Ct))).Outcome.Should().Be(ConfirmHoldOutcome.Confirmed);

        (await system.QueryAsync<Guid>("SELECT seat_id FROM inventory.seats WHERE order_id = @p0 AND status = 'Sold'", orderId))
            .Should().BeEquivalentTo(sale.Seats.Take(2));
    }

    [Fact]
    public async Task ConfirmHold_after_someone_took_a_seat_reports_seats_lost_every_time()
    {
        var (sale, fan, holdId) = await HeldAsync(seats: 2);
        var orderId = Guid.NewGuid();
        await ApiAsync(api => api.BeginCheckoutAsync(holdId, orderId, fan, Ct));

        // The backstop passes and another fan takes one of the seats.
        await system.ExpireHoldAsync(holdId);
        using var ben = system.PublicApi(Callers.Fan());
        await (await ben.HoldAsync(sale.EventId, sale.Seats[1])).PlacedAsync();

        (await ApiAsync(api => api.ConfirmHoldAsync(holdId, orderId, Ct))).Outcome.Should().Be(ConfirmHoldOutcome.SeatsLost);
        (await ApiAsync(api => api.ConfirmHoldAsync(holdId, orderId, Ct))).Outcome.Should().Be(ConfirmHoldOutcome.SeatsLost);

        (await system.ScalarAsync<long>("SELECT count(*) FROM inventory.seats WHERE order_id = @p0", orderId)).Should().Be(0, "nothing is sold to a lost hold");
    }

    [Fact]
    public async Task ConfirmHold_after_expiry_still_sells_seats_nobody_took_even_once_swept()
    {
        var (sale, fan, holdId) = await HeldAsync(seats: 2);
        var orderId = Guid.NewGuid();
        await ApiAsync(api => api.BeginCheckoutAsync(holdId, orderId, fan, Ct));

        await system.ExpireHoldAsync(holdId);
        await system.SweepNowAsync();
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(sale.Seats.Take(2));

        (await ApiAsync(api => api.ConfirmHoldAsync(holdId, orderId, Ct))).Outcome.Should().Be(ConfirmHoldOutcome.Confirmed);
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().NotContain(sale.Seats.Take(2));
    }

    [Fact]
    public async Task ReleaseHold_frees_the_seats_and_is_safe_to_repeat()
    {
        var (sale, fan, holdId) = await HeldAsync(seats: 2);
        var orderId = Guid.NewGuid();
        await ApiAsync(api => api.BeginCheckoutAsync(holdId, orderId, fan, Ct));

        (await ApiAsync(api => api.ReleaseHoldAsync(holdId, orderId, Ct))).Outcome.Should().Be(ReleaseHoldOutcome.Released);
        (await ApiAsync(api => api.ReleaseHoldAsync(holdId, orderId, Ct))).Outcome.Should().Be(ReleaseHoldOutcome.Released);
        (await system.AvailabilityAsync(sale.EventId)).AvailableSeatIds.Should().Contain(sale.Seats.Take(2));

        // A payment confirmed after the release can't have the seats any more: the order refunds.
        (await ApiAsync(api => api.ConfirmHoldAsync(holdId, orderId, Ct))).Outcome.Should().Be(ConfirmHoldOutcome.SeatsLost);
    }

    [Fact]
    public async Task ReleaseHold_after_the_payment_went_through_reports_already_confirmed()
    {
        var (_, fan, holdId) = await HeldAsync();
        var orderId = Guid.NewGuid();
        await ApiAsync(api => api.BeginCheckoutAsync(holdId, orderId, fan, Ct));
        await ApiAsync(api => api.ConfirmHoldAsync(holdId, orderId, Ct));

        (await ApiAsync(api => api.ReleaseHoldAsync(holdId, orderId, Ct))).Outcome.Should().Be(ReleaseHoldOutcome.AlreadyConfirmed);
        (await system.ScalarAsync<long>("SELECT count(*) FROM inventory.seats WHERE order_id = @p0 AND status = 'Sold'", orderId)).Should().Be(1);
    }

    [Fact]
    public async Task The_sweeper_shows_expired_seats_as_available_and_frees_the_fan_for_a_new_hold()
    {
        var (sale, fan, holdId) = await HeldAsync(seats: 2);
        var orderId = Guid.NewGuid();
        await ApiAsync(api => api.BeginCheckoutAsync(holdId, orderId, fan, Ct));

        await system.ExpireHoldAsync(holdId);
        await system.SweepNowAsync();

        (await system.QueryAsync<string>("SELECT status FROM inventory.seats WHERE hold_id = @p0", holdId)).Should().OnlyContain(status => status == "Available");
        (await system.ScalarAsync<string>("SELECT status FROM inventory.holds WHERE id = @p0", holdId)).Should().Be("Expired");

        using var again = system.PublicApi(TestTokens.Create(fan, ["fan"]));
        await (await again.HoldAsync(sale.EventId, sale.Seats[5])).PlacedAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<T> ApiAsync<T>(Func<IInventoryApi, Task<T>> call) =>
        system.InWorkersAsync(services => call(services.GetRequiredService<IInventoryApi>()));

    private async Task<(Sale Sale, string Fan, Guid HoldId)> HeldAsync(int seats = 1)
    {
        var sale = await system.OnSaleAsync();
        var fan = "fan-" + Guid.NewGuid().ToString("N");
        using var client = system.PublicApi(TestTokens.Create(fan, ["fan"]));
        var hold = await (await client.HoldAsync(sale.EventId, [.. sale.Seats.Take(seats)])).PlacedAsync();
        return (sale, fan, hold.HoldId);
    }
}
