using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using k8s;
using k8s.Models;

namespace EventTicketing.Tooling.ScalingLab;

/// <summary>One PostgreSQL instance of the product's CloudNativePG cluster.</summary>
/// <param name="Role">Primary (takes the writes), Standby (follows it, and answers browsing), Starting or Stopping.</param>
/// <param name="LagSeconds">How far a standby's replay is behind the primary, as the primary reports it.</param>
/// <param name="Connections">Backends on the instance for the product's database.</param>
public sealed record DatabaseInstance(string Name, string Role, string? Zone, double? LagSeconds, int? Connections);

/// <summary>PgBouncer in front of the primary (D49): its clients (the hosts' connections) and its server connections.</summary>
public sealed record PoolerState(int ClientsActive, int ClientsWaiting, int Servers);

/// <summary>The cluster as CloudNativePG reports it, with Prometheus' numbers for lag and connections.</summary>
/// <param name="Phase">CloudNativePG's own words, e.g. "Cluster in healthy state" or "Failing over".</param>
/// <param name="Timeline">PostgreSQL's timeline: one more at every promotion. (Each instance's own, as CloudNativePG
/// reports it, is its last checkpoint's: a standby's lags behind for a while after a failover, and means nothing.)</param>
public sealed record DatabaseState(
    string Phase,
    bool Healthy,
    string? Primary,
    DateTimeOffset? PrimarySince,
    int? Timeline,
    IReadOnlyList<DatabaseInstance> Instances,
    PoolerState? Pooler);

/// <summary>
/// The product's database as the Scaling Lab shows it (SL3): read from the Kubernetes API
/// (CloudNativePG's Cluster, its pods and their nodes' zones) and from Prometheus (lag and
/// connections), and the failover drill, which crashes the primary as `lab drill failover` does.
/// Only inside the lab cluster.
/// </summary>
public sealed class DatabaseView(IConfiguration configuration, HttpClient prometheus) : IDisposable
{
    public const string Cluster = "eventticketing";

    private const string CloudNativePg = "postgresql.cnpg.io";
    private const string Instances = $"cnpg.io/cluster={Cluster},cnpg.io/podRole=instance";
    private const string Zone = "topology.kubernetes.io/zone";

    private readonly Kubernetes? _kubernetes = KubernetesClientConfiguration.IsInCluster()
        ? new Kubernetes(KubernetesClientConfiguration.InClusterConfig())
        : null;

    private readonly string _namespace = configuration["PRODUCT_NAMESPACE"] is { Length: > 0 } space ? space : "eventticketing";

    public bool Available => _kubernetes is not null;

    public async Task<DatabaseState?> ReadAsync(CancellationToken cancellationToken)
    {
        if (_kubernetes is null)
        {
            return null;
        }

        var cluster = await _kubernetes.CustomObjects.GetNamespacedCustomObjectAsync<JsonElement>(CloudNativePg, "v1", _namespace, "clusters", Cluster, cancellationToken);
        var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(_namespace, labelSelector: Instances, cancellationToken: cancellationToken);
        var nodes = await _kubernetes.CoreV1.ListNodeAsync(cancellationToken: cancellationToken);
        var zones = nodes.Items.ToDictionary(node => node.Metadata.Name, node => Label(node.Metadata, Zone) ?? "", StringComparer.Ordinal);

        // Prometheus may be restarting: the panel then shows the cluster without its numbers.
        var lags = await ByLabelAsync("cnpg_pg_stat_replication_replay_lag_seconds", "application_name", cancellationToken);
        var connections = await ByLabelAsync($"sum by (pod) (cnpg_backends_total{{datname=\"{Cluster}\"}})", "pod", cancellationToken);
        var pooler = await PoolerAsync(cancellationToken);

        return FromCluster(cluster, pods.Items, zones, lags, connections, pooler);
    }

    /// <summary>
    /// The failover drill (D59): the primary's pod is deleted at once, as a crash would leave it, with
    /// no time to finish anything. CloudNativePG promotes a standby, and the -rw service follows it.
    /// </summary>
    /// <returns>The instance that was primary.</returns>
    public async Task<string> CrashPrimaryAsync(CancellationToken cancellationToken)
    {
        if (_kubernetes is null)
        {
            throw new InvalidOperationException("The database can only be drilled in the lab cluster.");
        }

        var cluster = await _kubernetes.CustomObjects.GetNamespacedCustomObjectAsync<JsonElement>(CloudNativePg, "v1", _namespace, "clusters", Cluster, cancellationToken);
        var primary = Text(cluster, "status", "currentPrimary")
            ?? throw new InvalidOperationException("CloudNativePG names no primary right now.");
        await _kubernetes.CoreV1.DeleteNamespacedPodAsync(primary, _namespace, new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: cancellationToken);
        return primary;
    }

    public void Dispose() => _kubernetes?.Dispose();

    /// <summary>The cluster as the lab shows it: each instance's role from its pod, CloudNativePG's phase and primary, Prometheus' numbers.</summary>
    public static DatabaseState FromCluster(
        JsonElement cluster,
        IEnumerable<V1Pod> pods,
        IReadOnlyDictionary<string, string> zones,
        IReadOnlyDictionary<string, double> lags,
        IReadOnlyDictionary<string, double> connections,
        PoolerState? pooler)
    {
        var instances = pods
            .OrderBy(pod => pod.Metadata.Name, StringComparer.Ordinal)
            .Select(pod =>
            {
                var name = pod.Metadata.Name;
                var ready = pod.Status?.Conditions?.Any(condition => condition.Type == "Ready" && condition.Status == "True") == true;
                var role = pod.Metadata.DeletionTimestamp is not null ? "Stopping"
                    : !ready ? "Starting"
                    : Label(pod.Metadata, "cnpg.io/instanceRole") == "primary" ? "Primary"
                    : "Standby";
                return new DatabaseInstance(
                    name,
                    role,
                    pod.Spec?.NodeName is { } node ? zones.GetValueOrDefault(node) : null,
                    role == "Standby" && lags.TryGetValue(name, out var lag) ? lag : null,
                    connections.TryGetValue(name, out var count) ? (int)count : null);
            })
            .ToList();

        var phase = Text(cluster, "status", "phase") ?? "Unknown";
        var since = Text(cluster, "status", "currentPrimaryTimestamp") is { } stamp
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;

        return new DatabaseState(
            phase,
            phase == "Cluster in healthy state",
            Text(cluster, "status", "currentPrimary"),
            since,
            Find(cluster, "status", "timelineID") is { ValueKind: JsonValueKind.Number } timeline ? timeline.GetInt32() : null,
            instances,
            pooler);
    }

    private async Task<PoolerState?> PoolerAsync(CancellationToken cancellationToken)
    {
        var active = await SumAsync($"cnpg_pgbouncer_pools_cl_active{{database=\"{Cluster}\"}}", cancellationToken);
        var waiting = await SumAsync($"cnpg_pgbouncer_pools_cl_waiting{{database=\"{Cluster}\"}}", cancellationToken);
        var servers = await SumAsync($"cnpg_pgbouncer_pools_sv_active{{database=\"{Cluster}\"}} + cnpg_pgbouncer_pools_sv_idle{{database=\"{Cluster}\"}}", cancellationToken);
        return active is null || waiting is null || servers is null ? null : new PoolerState((int)active, (int)waiting, (int)servers);
    }

    private async Task<double?> SumAsync(string query, CancellationToken cancellationToken)
    {
        var values = await QueryAsync($"sum({query})", cancellationToken);
        return values is [var (_, value), ..] ? value : null;
    }

    private async Task<IReadOnlyDictionary<string, double>> ByLabelAsync(string query, string label, CancellationToken cancellationToken)
    {
        var values = await QueryAsync(query, cancellationToken) ?? [];
        return values
            .Where(sample => sample.Labels.TryGetProperty(label, out _))
            .GroupBy(sample => sample.Labels.GetProperty(label).GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(sample => sample.Value), StringComparer.Ordinal);
    }

    /// <summary>An instant query: each series' labels and value, or null if Prometheus doesn't answer.</summary>
    private async Task<List<(JsonElement Labels, double Value)>?> QueryAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await prometheus.GetFromJsonAsync<JsonElement>(new Uri($"api/v1/query?query={Uri.EscapeDataString(query)}", UriKind.Relative), cancellationToken);
            return [.. answer.GetProperty("data").GetProperty("result").EnumerateArray()
                .Select(series => (series.GetProperty("metric"), double.Parse(series.GetProperty("value")[1].GetString()!, CultureInfo.InvariantCulture)))];
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static string? Label(V1ObjectMeta metadata, string name) =>
        metadata.Labels is { } labels && labels.TryGetValue(name, out var value) ? value : null;

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
}
