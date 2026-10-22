using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using k8s;
using static EventTicketing.Tooling.Lab.SystemTests.LabCluster;

namespace EventTicketing.Tooling.Lab.SystemTests;

/// <summary>
/// M3 Q8 (D66): the M3 demo (docs/demos/m3.md) in the lab cluster, the steps no other part of the
/// lab workflow already proves. The workflow's own steps do the rest: the failover drill (browsing
/// goes on without the primary), the zone drill, T8 with 200 door devices, a backup and its
/// restore. The autoscaler and the slow database are the M2 dry run's and SlowDatabaseTests'.
/// Each test leaves the lab as `lab up` left it.
/// </summary>
public sealed class M3DemoDryRunTests
{
    [Fact]
    public async Task Step_1_three_zones_each_with_a_database_instance_and_two_of_the_fan_site()
    {
        RequireLab();
        using var kubernetes = Kubernetes();
        var zones = (await kubernetes.CoreV1.ListNodeAsync(cancellationToken: Ct)).Items
            .ToDictionary(node => node.Metadata.Name, node => node.Metadata.Labels["topology.kubernetes.io/zone"]);
        zones.Values.Should().OnlyHaveUniqueItems().And.HaveCount(3, "a node per zone (D47)");

        var instances = (await kubernetes.CoreV1.ListNamespacedPodAsync("eventticketing", labelSelector: "cnpg.io/cluster=eventticketing,cnpg.io/instanceRole", cancellationToken: Ct)).Items;
        instances.Select(pod => pod.Metadata.Labels["cnpg.io/instanceRole"]).Should().BeEquivalentTo(["primary", "replica", "replica"], "a primary and two standbys (D48)");
        instances.Select(pod => zones[pod.Spec.NodeName]).Should().OnlyHaveUniqueItems("one database instance per zone");

        foreach (var (deployment, count) in new[] { ("fansite", 2), ("eventticketing-pooler-rw", 2), ("valkey", 1) })
        {
            var ready = (await kubernetes.AppsV1.ReadNamespacedDeploymentAsync(deployment, "eventticketing", cancellationToken: Ct)).Status.ReadyReplicas ?? 0;
            ready.Should().Be(count, $"{deployment} runs {count} (FS2, PG1, VK1)");
        }
    }

    /// <summary>
    /// VK1, RR1: 200 browsing requests/s are served by the cache and the replica. The primary's
    /// transactions per second hardly move (710/s before M3, for the same load); what it does commit
    /// is the outbox's and the sweepers' work, measured with no load first.
    /// </summary>
    [Fact]
    public async Task Step_3_browsing_doesnt_reach_the_primary()
    {
        RequireLab();
        try
        {
            await LoadAsync(0);
            var idle = await PrimaryCommitsPerSecondAsync(TimeSpan.FromSeconds(30));

            await LoadAsync(200, holdShare: 0);
            await Task.Delay(TimeSpan.FromSeconds(15), Ct); // the cache fills, the replicas' pools open
            var browsing = await PrimaryCommitsPerSecondAsync(TimeSpan.FromSeconds(30));
            var totals = await TotalsAsync(30);

            totals.Completed.Should().BeGreaterThan(3000, "the load was there: 200 requests/s for 30 s");
            totals.Failed.Should().Be(0);
            (browsing - idle).Should().BeLessThan(20,
                $"browsing comes from the cache and the replica: the primary committed {idle:0.0}/s idle and {browsing:0.0}/s under 200 browsing requests/s");
        }
        finally
        {
            await RestoreAsync();
        }
    }

    /// <summary>
    /// SC1 (D62): the scanner is served by CheckInApi, as a PWA, and signs a device in through it:
    /// the page needn't reach Keycloak. A revoked device can't sign in again.
    /// </summary>
    [Fact]
    public async Task Step_8_the_scanner_is_served_and_signs_a_device_in_through_checkinapi()
    {
        RequireLab();
        using var checkIn = Client(CheckInApi);
        var page = await checkIn.GetStringAsync(new Uri("/scanner/", UriKind.Relative), Ct);
        page.Should().Contain("<base href=\"/scanner/\"").And.Contain("blazor.webassembly");
        foreach (var asset in new[] { "/scanner/manifest.webmanifest", "/scanner/service-worker.js" })
        {
            (await checkIn.GetAsync(new Uri(asset, UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.OK, asset);
        }

        using var anonymous = Client(PublicApi);
        var events = await anonymous.GetFromJsonAsync<JsonElement>(new Uri("/events?q=%22Demo%20Concert%22", UriKind.Relative), Ct);
        var eventId = events.GetProperty("items")[0].GetProperty("eventId").GetGuid();
        using var organizer = Client(BackOfficeApi, await TokenAsync("olga"));
        var device = await (await organizer.PostJsonAsync($"/events/{eventId}/devices", new { name = "Dry run gate" })).CreatedAsync();
        var credentials = new { clientId = device.GetProperty("clientId").GetString(), clientSecret = device.GetProperty("clientSecret").GetString() };

        var signedIn = await (await checkIn.PostJsonAsync("/device-token", credentials)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        using var door = Client(CheckInApi, signedIn.GetProperty("accessToken").GetString());
        var doorList = await door.GetFromJsonAsync<JsonElement>(new Uri($"/events/{eventId}/door-list", UriKind.Relative), Ct);
        doorList.GetProperty("eventName").GetString().Should().Be("Demo Concert");

        (await organizer.DeleteAsync(new Uri($"/events/{eventId}/devices/{device.GetProperty("deviceId").GetGuid()}", UriKind.Relative), Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await checkIn.PostJsonAsync("/device-token", credentials)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The primary's committed transactions per second over a while. PostgreSQL's statistics reach
    /// pg_stat_database a moment after each transaction, so the window is long enough for that not to matter.
    /// </summary>
    private static async Task<double> PrimaryCommitsPerSecondAsync(TimeSpan window)
    {
        const string Commits = "SELECT xact_commit FROM pg_stat_database WHERE datname = 'eventticketing'";
        var clock = Stopwatch.StartNew();
        var before = long.Parse(await SqlAsync(Commits), CultureInfo.InvariantCulture);
        await Task.Delay(window, Ct);
        var after = long.Parse(await SqlAsync(Commits), CultureInfo.InvariantCulture);
        return (after - before) / clock.Elapsed.TotalSeconds;
    }
}
