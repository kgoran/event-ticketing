using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace EventTicketing.Tooling.ScalingLab;

/// <summary>One instance (pod) of a host.</summary>
/// <param name="State">Ready, Starting or Stopping.</param>
public sealed record Instance(string Name, string State, int Restarts, DateTimeOffset? StartedAt);

/// <summary>A host of the product: how many instances it should have, how many are ready, and each one.</summary>
public sealed record HostInstances(string Name, int Wanted, int Ready, IReadOnlyList<Instance> Instances);

/// <summary>How soon the autoscaler gives instances back (ADR-0019's profiles).</summary>
public enum ScalingProfile
{
    /// <summary>After 30 s below target, so a demo shows scaling in as well as out.</summary>
    Demo,

    /// <summary>After 5 minutes, Kubernetes' default: a short dip doesn't remove instances needed again soon.</summary>
    Realistic,
}

/// <summary>
/// A host's autoscaler (ADR-0054): on or off, its limits and profile, and while on, what it measures
/// and the count it wants.
/// </summary>
/// <param name="Target">The requests in progress per instance it aims for (ADR-0019, D56).</param>
/// <param name="InProgress">Requests in progress per instance, averaged over half a minute; null until measured.</param>
/// <param name="Current">The instances the autoscaler counted when it measured.</param>
public sealed record Autoscaler(
    bool On,
    ScalingProfile Profile,
    int Min,
    int Max,
    double Target,
    double? InProgress = null,
    int? Current = null,
    int? Wanted = null,
    DateTimeOffset? LastScaled = null);

/// <summary>
/// The product's hosts as Kubernetes runs them (ADR-0017 update, D31): read from the Kubernetes API
/// by label, and the instance count changed through the deployment's scale, as an operator would, or
/// left to KEDA's autoscaler (ADR-0054). Only inside the lab cluster: under Aspire there is nothing to show.
/// </summary>
public sealed class Instances : IDisposable
{
    /// <summary>The label every host of the product carries (product/deploy/chart).</summary>
    private const string ProductHosts = "app.kubernetes.io/part-of=eventticketing";

    // KEDA's ScaledObject (product/deploy/chart/templates/autoscaling.yaml) and its switch.
    private const string Keda = "keda.sh";
    private const string KedaVersion = "v1alpha1";
    private const string ScaledObjects = "scaledobjects";
    private const string Paused = "autoscaling.keda.sh/paused";

    /// <summary>Named, so Kubernetes records who set a count or flipped the switch.</summary>
    private const string FieldManager = "scaling-lab";

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
        // A deploy keeps the count that runs (product/deploy/chart).
        await _kubernetes.AppsV1.PatchNamespacedDeploymentScaleAsync(patch, host, _namespace, fieldManager: FieldManager, cancellationToken: cancellationToken);
    }

    /// <summary>The host's autoscaler, or null if it has none (no ScaledObject, or not in the cluster).</summary>
    public async Task<Autoscaler?> AutoscalerAsync(string host, CancellationToken cancellationToken)
    {
        if (_kubernetes is null)
        {
            return null;
        }

        JsonElement scaledObject;
        try
        {
            scaledObject = await _kubernetes.CustomObjects.GetNamespacedCustomObjectAsync<JsonElement>(Keda, KedaVersion, _namespace, ScaledObjects, host, cancellationToken);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        // While on, KEDA runs a Kubernetes autoscaler (HPA) for it, which holds the measurements.
        V2HorizontalPodAutoscaler? measured = null;
        if (!IsPaused(scaledObject) && Text(scaledObject, "status", "hpaName") is { } name)
        {
            try
            {
                measured = await _kubernetes.AutoscalingV2.ReadNamespacedHorizontalPodAutoscalerAsync(name, _namespace, cancellationToken: cancellationToken);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Just switched on: KEDA hasn't made it yet.
            }
        }

        return FromKeda(scaledObject, measured);
    }

    /// <summary>
    /// Switches the autoscaler on, or off (KEDA's pause, D39). Off keeps the count where it is, and
    /// − / + set it again; on, the autoscaler starts from the count that runs.
    /// </summary>
    public Task SetAutoAsync(string host, bool on, CancellationToken cancellationToken) =>
        PatchScaledObjectAsync(host, "{\"metadata\":{\"annotations\":{\"" + Paused + "\":" + (on ? "null" : "\"true\"") + "}}}", cancellationToken);

    /// <summary>How long the autoscaler waits below target before it gives instances back.</summary>
    public Task SetProfileAsync(string host, ScalingProfile profile, CancellationToken cancellationToken) =>
        PatchScaledObjectAsync(
            host,
            "{\"spec\":{\"advanced\":{\"horizontalPodAutoscalerConfig\":{\"behavior\":{\"scaleDown\":{\"stabilizationWindowSeconds\":"
                + ScaleDownSeconds(profile).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}}}}",
            cancellationToken);

    public static int ScaleDownSeconds(ScalingProfile profile) => profile == ScalingProfile.Demo ? 30 : 300;

    private async Task PatchScaledObjectAsync(string host, string patch, CancellationToken cancellationToken)
    {
        if (_kubernetes is null)
        {
            throw new InvalidOperationException("Autoscaling can only be changed in the lab cluster.");
        }

        await _kubernetes.CustomObjects.PatchNamespacedCustomObjectAsync(
            new V1Patch(patch, V1Patch.PatchType.MergePatch), Keda, KedaVersion, _namespace, ScaledObjects, host,
            fieldManager: FieldManager, cancellationToken: cancellationToken);
    }

    public void Dispose() => _kubernetes?.Dispose();

    /// <summary>The autoscaler as the lab shows it: KEDA's settings, and the Kubernetes autoscaler's measurements while on.</summary>
    public static Autoscaler FromKeda(JsonElement scaledObject, V2HorizontalPodAutoscaler? measured)
    {
        var spec = scaledObject.GetProperty("spec");
        var on = !IsPaused(scaledObject);
        var scaleDown = Number(spec, "advanced", "horizontalPodAutoscalerConfig", "behavior", "scaleDown", "stabilizationWindowSeconds") ?? 300;
        var target = spec.TryGetProperty("triggers", out var triggers)
            ? triggers.EnumerateArray().Where(trigger => Text(trigger, "type") == "prometheus").Select(trigger => Text(trigger, "metadata", "threshold")).FirstOrDefault()
            : null;
        // KEDA hands Kubernetes the total; Kubernetes reports it per instance.
        var inProgress = measured?.Status?.CurrentMetrics?
            .FirstOrDefault(metric => metric.External is not null)?.External?.Current?.AverageValue?.ToDecimal();

        return new Autoscaler(
            on,
            scaleDown < ScaleDownSeconds(ScalingProfile.Realistic) ? ScalingProfile.Demo : ScalingProfile.Realistic,
            Number(spec, "minReplicaCount") ?? 1,
            Number(spec, "maxReplicaCount") ?? 100,
            double.TryParse(target, System.Globalization.CultureInfo.InvariantCulture, out var perInstance) ? perInstance : 0,
            on && inProgress is { } value ? (double)value : null,
            on ? measured?.Status?.CurrentReplicas : null,
            on ? measured?.Status?.DesiredReplicas : null,
            on && measured?.Status?.LastScaleTime is { } scaled ? new DateTimeOffset(scaled, TimeSpan.Zero) : null);
    }

    private static bool IsPaused(JsonElement scaledObject) => Text(scaledObject, "metadata", "annotations", Paused) == "true";

    private static JsonElement? Find(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element;
    }

    private static string? Text(JsonElement element, params string[] path) =>
        Find(element, path) is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;

    private static int? Number(JsonElement element, params string[] path) =>
        Find(element, path) is { ValueKind: JsonValueKind.Number } found ? found.GetInt32() : null;

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
            "The Scaling Lab may not read, scale or autoscale the product's hosts: check its role in tooling/deploy/chart.",
        _ => exception.Message,
    };
}
