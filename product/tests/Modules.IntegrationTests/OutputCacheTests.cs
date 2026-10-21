using System.Net;
using System.Net.Http.Json;
using EventTicketing.Catalog.Browse;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// D52, ADR-0028: browsing's answers are the same for everyone, so the shared output cache serves
/// them, to fans signed in or not (in memory here; in Valkey where the host has it).
/// </summary>
public sealed class OutputCacheTests(TestSystem system)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_second_look_at_an_event_page_comes_from_the_cache_signed_in_or_not()
    {
        var sale = await system.OnSaleAsync();
        var page = new Uri($"/events/{sale.EventId}", UriKind.Relative);

        using var anonymous = system.PublicApi();
        using var fan = system.PublicApi(Callers.Fan());
        var first = await anonymous.GetAsync(page, CancellationToken);
        var again = await anonymous.GetAsync(page, CancellationToken);
        var signedIn = await fan.GetAsync(page, CancellationToken);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        again.Headers.Age.Should().NotBeNull("the output cache served it");
        signedIn.Headers.Age.Should().NotBeNull("a fan's token doesn't change what the page says");
        (await signedIn.Content.ReadAsStringAsync(CancellationToken)).Should().Be(await first.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    public async Task A_page_is_cached_no_longer_than_until_its_on_sale_opens()
    {
        // Far enough ahead for a busy test run to set the event up before it opens.
        var sale = await system.OnSaleAsync(onSaleAt: DateTimeOffset.UtcNow.AddSeconds(15));
        var page = new Uri($"/events/{sale.EventId}", UriKind.Relative);
        using var anonymous = system.PublicApi();

        var before = await anonymous.GetAsync(page, CancellationToken);
        var scheduled = (await before.Content.ReadFromJsonAsync<EventPage>(CancellationToken))!;
        scheduled.SalesPhase.Should().Be("Scheduled");
        var opensIn = scheduled.OnSaleAt!.Value - DateTimeOffset.UtcNow;
        before.Headers.CacheControl!.MaxAge.Should().BeLessThanOrEqualTo(opensIn + TimeSpan.FromSeconds(1), "a browser mustn't keep it past the opening either");

        await Task.Delay(opensIn + TimeSpan.FromSeconds(1), CancellationToken);

        var after = await anonymous.GetAsync(page, CancellationToken);
        (await after.Content.ReadFromJsonAsync<EventPage>(CancellationToken))!.SalesPhase.Should().Be("OnSale", $"fans refreshing at the opening see it open (Age {after.Headers.Age})");
    }

    [Fact]
    public async Task An_event_that_isnt_there_is_never_cached()
    {
        using var anonymous = system.PublicApi();
        var missing = new Uri($"/events/{Guid.NewGuid()}", UriKind.Relative);

        (await anonymous.GetAsync(missing, CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var again = await anonymous.GetAsync(missing, CancellationToken);

        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        again.Headers.Age.Should().BeNull();
    }
}
