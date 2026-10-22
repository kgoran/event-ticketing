using System.Text.Json;
using k8s.Models;

namespace EventTicketing.Tooling.ScalingLab.Tests;

/// <summary>SL3: the database panel shows each instance as CloudNativePG and Prometheus report it.</summary>
public sealed class DatabaseViewTests
{
    private static readonly Dictionary<string, string> Zones = new() { ["eventticketing-control-plane"] = "zone-a", ["eventticketing-worker"] = "zone-b", ["eventticketing-worker2"] = "zone-c" };

    [Fact]
    public void A_healthy_cluster_has_one_primary_and_standbys_with_their_lag_zone_and_connections()
    {
        var state = DatabaseView.FromCluster(
            Cluster(phase: "Cluster in healthy state", primary: "eventticketing-2"),
            [Pod("eventticketing-1", "replica", "eventticketing-worker"), Pod("eventticketing-2", "primary", "eventticketing-worker2"), Pod("eventticketing-3", "replica", "eventticketing-control-plane")],
            Zones,
            new Dictionary<string, double> { ["eventticketing-1"] = 0.0016, ["eventticketing-3"] = 0.002 },
            new Dictionary<string, double> { ["eventticketing-1"] = 3, ["eventticketing-2"] = 67, ["eventticketing-3"] = 2 },
            new PoolerState(21, 0, 40));

        state.Healthy.Should().BeTrue();
        state.Primary.Should().Be("eventticketing-2");
        state.PrimarySince.Should().Be(new DateTimeOffset(2026, 10, 22, 1, 0, 0, TimeSpan.Zero));
        state.Instances.Should().Equal(
            new DatabaseInstance("eventticketing-1", "Standby", "zone-b", 0.0016, 3),
            new DatabaseInstance("eventticketing-2", "Primary", "zone-c", null, 67),
            new DatabaseInstance("eventticketing-3", "Standby", "zone-a", 0.002, 2));
        state.Timeline.Should().Be(10);
        state.Pooler.Should().Be(new PoolerState(21, 0, 40));
    }

    [Fact]
    public void While_failing_over_the_crashed_primary_is_stopping_and_the_cluster_is_not_healthy()
    {
        var crashed = Pod("eventticketing-2", "primary", "eventticketing-worker2");
        crashed.Metadata.DeletionTimestamp = new DateTime(2026, 10, 22, 1, 5, 0, DateTimeKind.Utc);

        var state = DatabaseView.FromCluster(
            Cluster(phase: "Failing over", primary: "eventticketing-2"),
            [Pod("eventticketing-1", "replica", "eventticketing-worker"), crashed, Pod("eventticketing-3", "replica", "eventticketing-control-plane", ready: false)],
            Zones, new Dictionary<string, double>(), new Dictionary<string, double>(), pooler: null);

        state.Healthy.Should().BeFalse();
        state.Phase.Should().Be("Failing over");
        state.Instances.Select(instance => instance.Role).Should().Equal("Standby", "Stopping", "Starting");
    }

    [Fact]
    public void Without_prometheus_the_instances_show_without_lag_or_connections()
    {
        var state = DatabaseView.FromCluster(
            Cluster(phase: "Cluster in healthy state", primary: "eventticketing-1"),
            [Pod("eventticketing-1", "primary", "eventticketing-worker")],
            Zones, new Dictionary<string, double>(), new Dictionary<string, double>(), pooler: null);

        state.Instances.Should().ContainSingle().Which.Should().Be(new DatabaseInstance("eventticketing-1", "Primary", "zone-b", null, null));
        state.Pooler.Should().BeNull();
    }

    /// <summary>A CloudNativePG Cluster as the Kubernetes API returns it, its status cut to what the panel reads.</summary>
    private static JsonElement Cluster(string phase, string primary) => JsonDocument.Parse($$"""
        {
          "apiVersion": "postgresql.cnpg.io/v1",
          "kind": "Cluster",
          "metadata": { "name": "eventticketing" },
          "status": {
            "phase": "{{phase}}",
            "currentPrimary": "{{primary}}",
            "currentPrimaryTimestamp": "2026-10-22T01:00:00.000000Z",
            "timelineID": 10
          }
        }
        """).RootElement;

    private static V1Pod Pod(string name, string role, string node, bool ready = true) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, Labels = new Dictionary<string, string> { ["cnpg.io/cluster"] = "eventticketing", ["cnpg.io/instanceRole"] = role } },
        Spec = new V1PodSpec { NodeName = node, Containers = [] },
        Status = new V1PodStatus { Conditions = [new V1PodCondition { Type = "Ready", Status = ready ? "True" : "False" }] },
    };
}
