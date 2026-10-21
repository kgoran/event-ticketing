using System.Text.Json;
using k8s.Models;

namespace EventTicketing.Tooling.ScalingLab.Tests;

/// <summary>D31, D39: the lab shows each instance's state and the autoscaler as Kubernetes and KEDA report them.</summary>
public sealed class InstancesTests
{
    [Fact]
    public void An_instance_is_ready_when_kubernetes_sends_it_traffic()
    {
        var instance = Instances.FromPod(Pod(ready: true, restarts: 0));

        instance.Should().Be(new Instance("publicapi-7d9f8-x2k4q", "Ready", 0, new DateTimeOffset(2026, 10, 21, 6, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void An_instance_that_is_not_ready_yet_is_starting_and_its_restarts_count()
    {
        Instances.FromPod(Pod(ready: false, restarts: 2)).Should().Match<Instance>(i => i.State == "Starting" && i.Restarts == 2);
    }

    [Fact]
    public void An_instance_being_deleted_is_stopping_even_while_it_still_answers()
    {
        var pod = Pod(ready: true, restarts: 0);
        pod.Metadata.DeletionTimestamp = new DateTime(2026, 10, 21, 6, 5, 0, DateTimeKind.Utc);

        Instances.FromPod(pod).State.Should().Be("Stopping");
    }

    [Fact]
    public void A_paused_scaled_object_is_auto_off_and_shows_no_measurements()
    {
        var autoscaler = Instances.FromKeda(ScaledObject(paused: true, scaleDownSeconds: 30), Measured(cpu: 140, current: 3, desired: 6));

        autoscaler.Should().Be(new Autoscaler(On: false, ScalingProfile.Demo, Min: 1, Max: 8, TargetCpuPercent: 70));
    }

    [Fact]
    public void While_on_the_autoscaler_shows_what_it_measures_and_wants()
    {
        var scaled = new DateTime(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

        var autoscaler = Instances.FromKeda(ScaledObject(paused: false, scaleDownSeconds: 300), Measured(cpu: 140, current: 3, desired: 6, scaled));

        autoscaler.Should().Be(new Autoscaler(true, ScalingProfile.Realistic, 1, 8, 70, CpuPercent: 140, Current: 3, Wanted: 6, LastScaled: new DateTimeOffset(scaled)));
    }

    [Fact]
    public void Just_switched_on_there_is_nothing_measured_yet()
    {
        Instances.FromKeda(ScaledObject(paused: false, scaleDownSeconds: 30), measured: null)
            .Should().Match<Autoscaler>(a => a.On && a.CpuPercent == null && a.Wanted == null);
    }

    [Fact]
    public void Each_profile_has_its_scale_down_wait()
    {
        Instances.ScaleDownSeconds(ScalingProfile.Demo).Should().Be(30);
        Instances.ScaleDownSeconds(ScalingProfile.Realistic).Should().Be(300);
    }

    /// <summary>A ScaledObject as the Kubernetes API returns it (product/deploy/chart/templates/autoscaling.yaml).</summary>
    private static JsonElement ScaledObject(bool paused, int scaleDownSeconds) => JsonDocument.Parse($$"""
        {
          "apiVersion": "keda.sh/v1alpha1",
          "kind": "ScaledObject",
          "metadata": { "name": "publicapi"{{(paused ? ", \"annotations\": { \"autoscaling.keda.sh/paused\": \"true\" }" : "")}} },
          "spec": {
            "scaleTargetRef": { "name": "publicapi" },
            "minReplicaCount": 1,
            "maxReplicaCount": 8,
            "advanced": { "horizontalPodAutoscalerConfig": { "behavior": { "scaleDown": { "stabilizationWindowSeconds": {{scaleDownSeconds}} } } } },
            "triggers": [ { "type": "cpu", "metricType": "Utilization", "metadata": { "value": "70" } } ]
          },
          "status": { "hpaName": "keda-hpa-publicapi" }
        }
        """).RootElement;

    private static V2HorizontalPodAutoscaler Measured(int cpu, int current, int desired, DateTime? scaled = null) => new()
    {
        Spec = new V2HorizontalPodAutoscalerSpec
        {
            MaxReplicas = 8,
            ScaleTargetRef = new V2CrossVersionObjectReference { Kind = "Deployment", Name = "publicapi" },
        },
        Status = new V2HorizontalPodAutoscalerStatus
        {
            CurrentReplicas = current,
            DesiredReplicas = desired,
            LastScaleTime = scaled,
            CurrentMetrics =
            [
                new V2MetricStatus
                {
                    Type = "Resource",
                    Resource = new V2ResourceMetricStatus { Name = "cpu", Current = new V2MetricValueStatus { AverageUtilization = cpu } },
                },
            ],
        },
    };

    private static V1Pod Pod(bool ready, int restarts) => new()
    {
        Metadata = new V1ObjectMeta { Name = "publicapi-7d9f8-x2k4q" },
        Status = new V1PodStatus
        {
            StartTime = new DateTime(2026, 10, 21, 6, 0, 0, DateTimeKind.Utc),
            Conditions = [new V1PodCondition { Type = "Ready", Status = ready ? "True" : "False" }],
            ContainerStatuses = [new V1ContainerStatus { Name = "publicapi", RestartCount = restarts, Ready = ready, Image = "x", ImageID = "x" }],
        },
    };
}
