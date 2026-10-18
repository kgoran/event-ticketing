using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace EventTicketing.Dev.AppHost.Tests;

/// <summary>M1a T1: one command prepares the demo. The seeder runs as the AppHost's "seed" resource.</summary>
public sealed class DemoSeederTests(RunningApp running)
{
    [Fact]
    public async Task The_seeder_prepares_an_event_that_is_on_sale()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var started = await running.App.ResourceCommands.ExecuteCommandAsync("seed", KnownResourceCommands.StartCommand, cancellationToken);
        started.Success.Should().BeTrue(started.Message);

        var seed = await running.App.ResourceNotifications.WaitForResourceAsync(
            "seed", e => e.Snapshot.State?.Text == KnownResourceStates.Finished, cancellationToken);
        seed.Snapshot.ExitCode.Should().Be(0);

        using var anonymous = running.Client("publicapi");
        var page = await anonymous.GetFromJsonAsync<JsonElement>(new Uri("/events?q=demo%20concert", UriKind.Relative), cancellationToken);
        page.GetProperty("items").EnumerateArray()
            .Should().Contain(e => e.GetProperty("name").GetString() == "Demo Concert"
                && e.GetProperty("venueName").GetString() == "Demo Arena"
                && e.GetProperty("salesPhase").GetString() == "OnSale");
    }
}
