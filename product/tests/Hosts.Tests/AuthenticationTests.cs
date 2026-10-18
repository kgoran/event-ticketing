using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EventTicketing.Shared.Infrastructure.Authentication;
using EventTicketing.Testing;
using Microsoft.IdentityModel.Tokens;

namespace EventTicketing.Hosts.Tests;

/// <summary>K1: every endpoint needs a valid token unless it opts out; the token says who the caller is.</summary>
public sealed class AuthenticationTests
{
    private static readonly Guid OrganizerId = Guid.Parse("0199f2a0-0000-7000-8000-000000000001");

    public static TheoryData<string> ApiHosts => ["PublicApi", "BackOfficeApi"];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(ApiHosts))]
    public async Task Liveness_and_the_OpenAPI_document_need_no_token(string host)
    {
        await using var api = HostUnderTest.Start(host);

        // /alive, not /health: readiness includes the database, which these in-memory hosts don't have.
        (await api.Client.GetAsync(new Uri("/alive", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.Client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(ApiHosts))]
    public async Task A_call_without_a_token_is_refused(string host)
    {
        await using var api = HostUnderTest.Start(host);

        var response = await api.Client.GetAsync(new Uri("/me", UriKind.Relative), CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(ApiHosts))]
    public async Task A_valid_token_says_who_the_caller_is(string host)
    {
        await using var api = HostUnderTest.Start(host);
        Authorize(api, TestTokens.Create("olga", roles: ["organizer"], organizerIds: [OrganizerId]));

        var caller = await api.Client.GetFromJsonAsync<CallerResponse>(new Uri("/me", UriKind.Relative), CancellationToken);

        caller!.UserId.Should().Be("olga");
        caller.Roles.Should().Equal("organizer");
        caller.OrganizerIds.Should().Equal(OrganizerId);
    }

    [Fact]
    public async Task Tokens_for_another_audience_expired_tokens_and_foreign_signatures_are_refused()
    {
        await using var api = HostUnderTest.Start("PublicApi");
        var foreignKey = new SymmetricSecurityKey(new byte[32]) { KeyId = "foreign" };

        string[] tokens =
        [
            TestTokens.Create("ana", roles: ["fan"], audience: "some-other-api"),
            TestTokens.Create("ana", roles: ["fan"], lifetime: TimeSpan.FromMinutes(-1)),
            TestTokens.Create("ana", roles: ["fan"], signingKey: foreignKey),
        ];

        foreach (var token in tokens)
        {
            Authorize(api, token);
            var response = await api.Client.GetAsync(new Uri("/me", UriKind.Relative), CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    private static void Authorize(HostUnderTest api, string token) =>
        api.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
