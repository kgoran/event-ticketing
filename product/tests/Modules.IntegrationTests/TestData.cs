using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using EventTicketing.Catalog.Events;
using EventTicketing.Catalog.Venues;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Testing;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>Tokens for the kinds of callers the tests need, each with fresh IDs unless given.</summary>
internal static class Callers
{
    public static string Organizer(Guid organizerId) =>
        TestTokens.Create("organizer-" + organizerId.ToString("N"), [Roles.Organizer, Roles.Fan], [organizerId]);

    public static string Fan(string? subject = null) =>
        TestTokens.Create(subject ?? "fan-" + Guid.NewGuid().ToString("N"), [Roles.Fan]);
}

internal static class Venues
{
    /// <summary>
    /// 2,000 seats in four sections A–D (20 rows of 25 each) and a standing area FLOOR for 1,000:
    /// the venue from the M1a demo.
    /// </summary>
    public static ImportVenue Arena(Guid organizerId) => new(
        organizerId,
        "Test Arena",
        "Europe/Zagreb",
        [
            .. "ABCD".Select((code, index) => new VenueSectionInput(
                code.ToString(),
                $"Tribune {code}",
                SectionKind.Seated,
                Rows: [.. Enumerable.Range(1, 20).Select(row => new VenueRowInput(row.ToString(CultureInfo.InvariantCulture), 25))],
                X: index * 30)),
            new VenueSectionInput("FLOOR", "Floor", SectionKind.Standing, Capacity: 1_000, Y: 25),
        ]);

    /// <summary>One section of rows × seats per row, for tests that only need a few seats.</summary>
    public static ImportVenue Small(Guid organizerId, int rows = 2, int seatsPerRow = 5) => new(
        organizerId,
        "Test Club",
        "Europe/Zagreb",
        [new VenueSectionInput("A", "Hall", SectionKind.Seated, Rows: [.. Enumerable.Range(1, rows).Select(row => new VenueRowInput(row.ToString(CultureInfo.InvariantCulture), seatsPerRow))])]);
}

/// <summary>The back-office steps every scenario starts with.</summary>
internal static class BackOffice
{
    public static async Task<Guid> ImportVenueAsync(this HttpClient backOffice, ImportVenue venue)
    {
        var response = await backOffice.PostAsJsonAsync(new Uri("/venues", UriKind.Relative), venue, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<VenueImported>(TestContext.Current.CancellationToken))!.VenueId;
    }

    public static async Task<Guid> CreateEventAsync(this HttpClient backOffice, CreateEvent command)
    {
        var response = await backOffice.PostAsJsonAsync(new Uri("/events", UriKind.Relative), command, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<EventCreated>(TestContext.Current.CancellationToken))!.EventId;
    }

    /// <summary>An event at <paramref name="venueId"/> with every section in one price category.</summary>
    public static CreateEvent SingleCategoryEvent(Guid organizerId, Guid venueId, IEnumerable<string> sections, long priceMinorUnits = 2500) => new(
        organizerId,
        venueId,
        "Test concert",
        DateTimeOffset.UtcNow.AddMonths(1),
        "EUR",
        [new PriceCategoryInput("Standard", priceMinorUnits)],
        [.. sections.Select(section => new PriceAssignmentInput(section, "Standard"))]);

    public static async Task<HttpResponseMessage> ScheduleOnSaleAsync(this HttpClient backOffice, Guid eventId, DateTimeOffset onSaleAt) =>
        await backOffice.PutAsJsonAsync(new Uri($"/events/{eventId}/on-sale", UriKind.Relative), new ScheduleOnSaleRequest(onSaleAt), TestContext.Current.CancellationToken);

    public static async Task<HttpResponseMessage> PublishAsync(this HttpClient backOffice, Guid eventId) =>
        await backOffice.PostAsync(new Uri($"/events/{eventId}/publish", UriKind.Relative), null, TestContext.Current.CancellationToken);

    public static async Task<EventDetails> EventDetailsAsync(this HttpClient backOffice, Guid eventId) =>
        (await backOffice.GetFromJsonAsync<EventDetails>(new Uri($"/events/{eventId}", UriKind.Relative), TestContext.Current.CancellationToken))!;
}
