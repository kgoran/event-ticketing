using EventTicketing.FanSite.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EventTicketing.FanSite.Tests;

public sealed class AuthenticationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tokens_from_the_identity_provider_expire_when_it_says()
    {
        var response = new OpenIdConnectMessage("""
            { "access_token": "a1", "refresh_token": "r1", "id_token": "i1", "expires_in": 300, "refresh_expires_in": 1800 }
            """);

        var tokens = FanTokens.From(response, Now);

        tokens.Should().Be(new FanTokens("a1", Now.AddMinutes(5), "r1", "i1"));
        FanTokens.SessionLifetime(response).Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void A_refresh_response_without_a_new_refresh_or_id_token_keeps_the_previous_ones()
    {
        var previous = new FanTokens("a1", Now, "r1", "i1");

        var refreshed = FanTokens.From(new OpenIdConnectMessage("""{ "access_token": "a2", "expires_in": 300 }"""), Now, previous);

        refreshed.Should().Be(new FanTokens("a2", Now.AddMinutes(5), "r1", "i1"));
    }

    [Fact]
    public void An_access_token_is_refreshed_before_it_can_expire_on_its_way_to_the_api()
    {
        var tokens = new FanTokens("a1", Now.AddMinutes(1), "r1", "i1");

        tokens.IsFreshAt(Now).Should().BeTrue();
        tokens.IsFreshAt(Now.AddSeconds(29)).Should().BeTrue();
        tokens.IsFreshAt(Now.AddSeconds(31)).Should().BeFalse();
    }

    [Fact]
    public async Task The_store_keeps_tokens_encrypted_and_treats_unreadable_ones_as_signed_out()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new FanTokenStore(cache, new EphemeralDataProtectionProvider());
        var tokens = new FanTokens("secret-access-token", Now, "r1", "i1");
        var ct = TestContext.Current.CancellationToken;

        await store.SaveAsync("s1", tokens, TimeSpan.FromMinutes(30), ct);

        (await store.GetAsync("s1", ct)).Should().Be(tokens);
        System.Text.Encoding.UTF8.GetString((await cache.GetAsync("fansite:tokens:s1", ct))!).Should().NotContain("secret-access-token");

        // Another instance with other keys (no shared key ring) can't read them.
        var otherInstance = new FanTokenStore(cache, new EphemeralDataProtectionProvider());
        (await otherInstance.GetAsync("s1", ct)).Should().BeNull();

        await store.RemoveAsync("s1", ct);
        (await store.GetAsync("s1", ct)).Should().BeNull();
    }

    [Theory]
    [InlineData("/events/42", "/events/42")]
    [InlineData("/orders/1?x=y", "/orders/1?x=y")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("events", "/")]
    public void Signing_in_and_out_only_ever_returns_to_this_site(string? returnUrl, string expected) =>
        AccountEndpoints.LocalUrl(returnUrl).Should().Be(expected);
}
