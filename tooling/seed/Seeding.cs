using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.Tooling.Clients.BackOffice;
using EventTicketing.Tooling.Clients.BackOffice.Models;
using EventTicketing.Tooling.Clients.Public;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Bundle;

namespace EventTicketing.Tooling.Seed;

/// <summary>Where the AppHost's services are: from the environment (the Aspire resource sets it), or the launch-profile ports.</summary>
internal sealed record SeedUrls(Uri BackOfficeApi, Uri PublicApi, Uri Keycloak)
{
    public static SeedUrls FromEnvironment() => new(
        Url("BACKOFFICEAPI_URL", "http://localhost:5102"),
        Url("PUBLICAPI_URL", "http://localhost:5101"),
        Url("KEYCLOAK_URL", "http://localhost:8180"));

    private static Uri Url(string variable, string fallback) =>
        new((Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : fallback).TrimEnd('/') + "/");
}

internal static class ApiClients
{
    public static BackOfficeApiClient BackOffice(Uri baseUrl, string accessToken) =>
        new(new DefaultRequestAdapter(new BaseBearerTokenAuthenticationProvider(new StaticToken(accessToken)))
        {
            BaseUrl = baseUrl.ToString().TrimEnd('/'),
        });

    public static PublicApiClient Public(Uri baseUrl) =>
        new(new DefaultRequestAdapter(new AnonymousAuthenticationProvider()) { BaseUrl = baseUrl.ToString().TrimEnd('/') });

    private sealed class StaticToken(string token) : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new();

        public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(token);
    }
}

/// <summary>Tokens from the development realm's password grant: users' passwords equal their names (dev/AppHost/Realms).</summary>
internal static class Tokens
{
    public static async Task<string> PasswordGrantAsync(HttpClient keycloak, string user, CancellationToken cancellationToken)
    {
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "eventticketing-dev",
            ["username"] = user,
            ["password"] = user,
        });

        using var response = await keycloak.PostAsync(new Uri("realms/eventticketing/protocol/openid-connect/token", UriKind.Relative), request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.GetProperty("access_token").GetString()!;
    }
}

/// <summary>The demo's venue and event (docs/milestones/m1a.md, demo script).</summary>
internal static class DemoData
{
    /// <summary>The organizer group seeded in the realm; olga is its member.</summary>
    public static readonly Guid OrganizerId = Guid.Parse("0199f2a0-0000-7000-8000-000000000001");

    public const int StandingCapacity = 500;

    /// <summary>2,000 seats and the standing floor.</summary>
    public const int Places = 2_000 + StandingCapacity;

    /// <summary>Four tribunes A–D of 20 rows × 25 seats, and a standing floor in front of them.</summary>
    public static ImportVenue Arena() => new()
    {
        OrganizerId = OrganizerId,
        Name = "Demo Arena",
        TimeZone = "Europe/Zagreb",
        Sections =
        [
            .. "ABCD".Select((code, index) => new VenueSectionInput
            {
                Code = code.ToString(),
                Name = $"Tribune {code}",
                Kind = SectionKind.Seated,
                X = index * 27,
                Y = 12,
                Rows = [.. Enumerable.Range(1, 20).Select(row => new VenueRowInput { Label = row.ToString(CultureInfo.InvariantCulture), Seats = 25, FirstNumber = 1 })],
            }),
            new VenueSectionInput { Code = "FLOOR", Name = "Floor", Kind = SectionKind.Standing, Capacity = StandingCapacity, X = 0, Y = 0 },
        ],
    };

    /// <summary>A month from now; the first five rows of each tribune are Premium.</summary>
    public static CreateEvent Concert(Guid venueId) => new()
    {
        OrganizerId = OrganizerId,
        VenueId = venueId,
        Name = "Demo Concert",
        StartsAt = DateTimeOffset.UtcNow.AddDays(30),
        Currency = "EUR",
        PriceCategories =
        [
            new PriceCategoryInput { Name = "Premium", PriceMinorUnits = 9_000 },
            new PriceCategoryInput { Name = "Standard", PriceMinorUnits = 5_000 },
            new PriceCategoryInput { Name = "Floor", PriceMinorUnits = 3_500 },
        ],
        Seating =
        [
            .. "ABCD".SelectMany(code => new[]
            {
                new PriceAssignmentInput { Section = code.ToString(), PriceCategory = "Premium", Rows = ["1", "2", "3", "4", "5"] },
                new PriceAssignmentInput { Section = code.ToString(), PriceCategory = "Standard" },
            }),
            new PriceAssignmentInput { Section = "FLOOR", PriceCategory = "Floor" },
        ],
    };
}
