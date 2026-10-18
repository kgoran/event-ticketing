using System.Diagnostics;
using System.Globalization;
using EventTicketing.Tooling.Clients.BackOffice;
using EventTicketing.Tooling.Clients.BackOffice.Models;
using EventTicketing.Tooling.Clients.Public;
using EventTicketing.Tooling.Seed;
using Microsoft.Kiota.Abstractions;

// Prepares the M1a demo against a running AppHost: registers the realm's organizer, imports a
// 2,000-seat venue with a standing floor, creates two events (the concert, and one whose prices pick
// LocalPay's failure scenarios), opens sales and publishes them, then waits until Inventory has
// their places. Run it with: dotnet run --project tooling/seed
// (or start the "seed" resource in the Aspire dashboard). Every run creates a new event.
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var cancellationToken = cancellation.Token;
var urls = SeedUrls.FromEnvironment();

Console.WriteLine($"Seeding through {urls.BackOfficeApi} (Keycloak {urls.Keycloak})");
using var keycloak = new HttpClient { BaseAddress = urls.Keycloak };
var admin = ApiClients.BackOffice(urls.BackOfficeApi, await Tokens.PasswordGrantAsync(keycloak, "adam", cancellationToken));
var organizer = ApiClients.BackOffice(urls.BackOfficeApi, await Tokens.PasswordGrantAsync(keycloak, "olga", cancellationToken));

// The realm's organizer group, registered under its own ID; already there after the first run.
try
{
    await admin.Organizers.PostAsync(new CreateOrganizer { OrganizerId = DemoData.OrganizerId, Name = "Demo Organizer" }, cancellationToken: cancellationToken);
    Console.WriteLine("Registered the demo organizer");
}
catch (ApiException exception) when (exception.ResponseStatusCode == 409)
{
    Console.WriteLine("The demo organizer is already registered");
}

var venue = await organizer.Venues.PostAsync(DemoData.Arena(), cancellationToken: cancellationToken)
    ?? throw new InvalidOperationException("The venue import returned nothing.");
Console.WriteLine($"Imported venue {venue.VenueId}");

var concert = await PublishOnSaleAsync(DemoData.Concert(venue.VenueId!.Value));
var scenarios = await PublishOnSaleAsync(DemoData.FailureScenarios(venue.VenueId!.Value));

// EventPublished travels through the outbox to Inventory; an event is ready when its places are there.
var fans = ApiClients.Public(urls.PublicApi);
var waited = Stopwatch.StartNew();
foreach (var eventId in new[] { concert, scenarios })
{
    while (true)
    {
        var availability = await fans.Events[eventId].Availability.GetAsync(cancellationToken: cancellationToken);
        var places = availability?.Sections?.Sum(section => section.Total ?? 0) ?? 0;
        if (availability?.OnSale == true && places == DemoData.Places)
        {
            break;
        }

        if (waited.Elapsed > TimeSpan.FromSeconds(90))
        {
            throw new TimeoutException($"Inventory has {places} of {DemoData.Places} places of event {eventId} after {waited.Elapsed.TotalSeconds:0} s.");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
    }
}

Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"""

    The demo is ready ({waited.Elapsed.TotalSeconds:0.0} s for Inventory):
      Demo Concert       {concert}
      Failure Scenarios  {scenarios}   (hold ONE seat: the price picks LocalPay's scenario)
      browse             {urls.PublicApi}events?q=demo
      event page         {urls.PublicApi}events/{concert}
      free seats         {urls.PublicApi}events/{concert}/availability
      API reference      {urls.PublicApi}scalar
    """));

async Task<Guid> PublishOnSaleAsync(CreateEvent command)
{
    var created = await organizer.Events.PostAsync(command, cancellationToken: cancellationToken)
        ?? throw new InvalidOperationException("Creating the event returned nothing.");
    var eventId = created.EventId!.Value;

    await organizer.Events[eventId].OnSale.PutAsync(new ScheduleOnSaleRequest { OnSaleAt = DateTimeOffset.UtcNow }, cancellationToken: cancellationToken);
    await organizer.Events[eventId].Publish.PostAsync(cancellationToken: cancellationToken);
    Console.WriteLine($"Published \"{command.Name}\" ({eventId}), on sale now");
    return eventId;
}
