using System.Net;
using System.Net.Http.Json;
using EventTicketing.Inventory.Application.Holds;
using EventTicketing.Shared.Infrastructure.Features;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>FF1: PauseHolds, an operator's brake on new holds (D45, docs/feature-flags.md).</summary>
public sealed class FeatureFlagTests(TestSystem system)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task While_holds_are_paused_new_ones_are_refused_for_now_and_everything_else_goes_on()
    {
        var sale = await system.OnSaleAsync();
        var fanWithAHold = Guid.NewGuid().ToString();
        HoldPlaced held;
        using (var before = system.PublicApi(Callers.Fan(fanWithAHold)))
        {
            held = await (await before.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync();
        }

        await using var paused = system.PublicApiWith(($"FeatureManagement:{FeatureFlags.PauseHolds}", "true"));
        using var fan = TestSystem.Client(paused, Callers.Fan());

        var refused = await fan.HoldAsync(sale.EventId, sale.Seats[1]);
        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        refused.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        var problem = await refused.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken);
        problem!.Title.Should().StartWith("New holds are paused");

        // Browsing goes on, and so does the hold a fan has already: it can still be checked out.
        using var anonymous = TestSystem.Client(paused);
        (await anonymous.GetAsync(new Uri($"/events/{sale.EventId}", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var holder = TestSystem.Client(paused, Callers.Fan(fanWithAHold));
        (await holder.CheckoutAsync(held.HoldId)).OrderId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task With_the_flag_off_holds_are_placed_as_always()
    {
        var sale = await system.OnSaleAsync();
        await using var notPaused = system.PublicApiWith(($"FeatureManagement:{FeatureFlags.PauseHolds}", "false"));
        using var fan = TestSystem.Client(notPaused, Callers.Fan());

        (await fan.HoldAsync(sale.EventId, sale.Seats[0])).StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
