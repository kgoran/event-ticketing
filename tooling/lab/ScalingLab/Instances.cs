using k8s;
using k8s.Autorest;
using k8s.Models;

namespace EventTicketing.Tooling.ScalingLab;

/// <summary>One instance (pod) of a host.</summary>
/// <param name="State">Ready, Starting or Stopping.</param>
public sealed record Instance(string Name, string State, int Restarts, DateTimeOffset? StartedAt);

/// <summary>A host of the product: how many instances it should have, how many are ready, and each one.</summary>
public sealed record HostInstances(string Name, int Wanted, int Ready, IReadOnlyList<Instance> Instances);

/// <summary>
/// The product's hosts as Kubernetes runs them (ADR-0017 update, D31): read from the Kubernetes API
/// by label, and the instance count changed through the deployment's scale, as an operator would and
/// as M5's autoscaler will. Only inside the lab cluster: under Aspire there is nothing to show.
/// </summary>
public sealed class Instances : IDisposable
{
    /// <summary>The label every host of the product carries (product/deploy/chart).</summary>
    private const string ProductHosts = "app.kubernetes.io/part-of=eventticketing";

    private readonly Kubernetes? _kubernetes;
    private readonly string _namespace;

    public Instances(IConfiguration configuration)
    {
        _namespace = configuration["PRODUCT_NAMESPACE"] is { Length: > 0 } space ? space : "eventticketing";
        if (KubernetesClientConfiguration.IsInCluster())
        {
            _kubernetes = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
        }
    }

    /// <summary>Whether the lab runs in a cluster, so there are instances to show.</summary>
    public bool Available => _kubernetes is not null;

    public const int MaxCount = 8;

    public async Task<IReadOnlyList<HostInstances>> ListAsync(CancellationToken cancellationToken)
    {
        if (_kubernetes is null)
        {
            return [];
        }

        var deployments = await _kubernetes.AppsV1.ListNamespacedDeploymentAsync(_namespace, labelSelector: ProductHosts, cancellationToken: cancellationToken);
        var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(_namespace, labelSelector: ProductHosts, cancellationToken: cancellationToken);

        return [.. deployments.Items
            .OrderBy(deployment => deployment.Metadata.Name, StringComparer.Ordinal)
            .Select(deployment => new HostInstances(
                deployment.Metadata.Name,
                deployment.Spec.Replicas ?? 0,
                deployment.Status.ReadyReplicas ?? 0,
                [.. pods.Items
                    .Where(pod => pod.Metadata.Labels?.TryGetValue("app.kubernetes.io/name", out var host) == true && host == deployment.Metadata.Name)
                    .OrderBy(pod => pod.Status.StartTime ?? DateTime.MaxValue)
                    .Select(FromPod)]))];
    }

    /// <summary>Sets a host's instance count; Kubernetes starts or stops instances to match.</summary>
    public async Task ScaleAsync(string host, int count, CancellationToken cancellationToken)
    {
        if (_kubernetes is null)
        {
            throw new InvalidOperationException("Instances can only be changed in the lab cluster.");
        }

        var replicas = Math.Clamp(count, 1, MaxCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var patch = new V1Patch("{\"spec\":{\"replicas\":" + replicas + "}}", V1Patch.PatchType.MergePatch);
        // Named, so Kubernetes records who set the count (Helm keeps it at the next deploy).
        await _kubernetes.AppsV1.PatchNamespacedDeploymentScaleAsync(patch, host, _namespace, fieldManager: "scaling-lab", cancellationToken: cancellationToken);
    }

    public void Dispose() => _kubernetes?.Dispose();

    /// <summary>An instance as the lab shows it: stopping once deletion has started, ready once Kubernetes sends it traffic.</summary>
    public static Instance FromPod(V1Pod pod)
    {
        ArgumentNullException.ThrowIfNull(pod);
        var ready = pod.Status.Conditions?.Any(condition => condition.Type == "Ready" && condition.Status == "True") == true;
        var state = pod.Metadata.DeletionTimestamp is not null ? "Stopping" : ready ? "Ready" : "Starting";
        var restarts = pod.Status.ContainerStatuses?.Sum(container => container.RestartCount) ?? 0;
        return new Instance(pod.Metadata.Name, state, restarts, pod.Status.StartTime is { } started ? new DateTimeOffset(started, TimeSpan.Zero) : null);
    }

    /// <summary>Why a call failed, in a sentence for the page.</summary>
    public static string Explain(Exception exception) => exception switch
    {
        HttpOperationException { Response.StatusCode: System.Net.HttpStatusCode.Forbidden } =>
            "The Scaling Lab may not read or scale the product's hosts: check its role in tooling/deploy/chart.",
        _ => exception.Message,
    };
}
