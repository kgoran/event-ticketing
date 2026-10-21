using k8s.Models;

namespace EventTicketing.Tooling.ScalingLab.Tests;

/// <summary>D31: the lab shows each instance's state as Kubernetes reports it.</summary>
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
