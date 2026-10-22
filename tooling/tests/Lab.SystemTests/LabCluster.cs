using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Models;

namespace EventTicketing.Tooling.Lab.SystemTests;

/// <summary>
/// The lab cluster from outside, as a presenter or an operator reaches it: the usual localhost
/// ports (ADR-0053) and the Kubernetes API through the lab's kubectl context.
/// </summary>
internal static class LabCluster
{
    public const string PublicApi = "http://localhost:5101";
    public const string Keycloak = "http://localhost:8180";
    public const string Generator = "http://localhost:5301";
    public const string Mailpit = "http://localhost:8025";

    private const string Product = "eventticketing";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Only where the lab runs: the lab workflow sets LAB_SYSTEM_TESTS=1 after `lab up`.</summary>
    public static void RequireLab()
    {
        if (Environment.GetEnvironmentVariable("LAB_SYSTEM_TESTS") != "1")
        {
            Assert.Skip("Needs the lab cluster: dotnet run lab.cs -- up, then LAB_SYSTEM_TESTS=1.");
        }
    }

    public static HttpClient Client(string baseAddress, string? token = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(30) };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return client;
    }

    /// <summary>A fan's token from Keycloak's development client, as the seeder and the load generator get theirs.</summary>
    public static async Task<string> TokenAsync(string user)
    {
        using var keycloak = Client(Keycloak);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = "eventticketing-dev", ["username"] = user, ["password"] = user,
        });
        using var response = await keycloak.PostAsync(new Uri("/realms/eventticketing/protocol/openid-connect/token", UriKind.Relative), form, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("access_token").GetString()!;
    }

    /// <summary>Probes until done; on timeout, says what didn't happen, and <paramref name="explain"/> what the system showed then.</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> probe, Func<T, bool> done, TimeSpan timeout, string what, Func<Task<string>>? explain = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var value = await probe();
            if (done(value))
            {
                return value;
            }

            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException($"Not within {timeout.TotalSeconds:0} s: {what}." + (explain is null ? "" : $" {await explain()}"));
            }

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }
    }

    // The load generator (ADR-0017)

    public static async Task LoadAsync(double requestsPerSecond, double holdShare = 0.2)
    {
        using var generator = Client(Generator);
        var settings = new { requestsPerSecond, holdShare };
        (await generator.PutAsJsonAsync(new Uri("/settings", UriKind.Relative), settings, Ct)).EnsureSuccessStatusCode();
    }

    /// <summary>The generator's last <paramref name="seconds"/> seconds, summed.</summary>
    public static async Task<Totals> TotalsAsync(int seconds)
    {
        using var generator = Client(Generator);
        var stats = await generator.GetFromJsonAsync<JsonElement>(new Uri($"/stats?seconds={seconds}", UriKind.Relative), Ct);
        var window = stats.GetProperty("seconds").EnumerateArray().ToList();
        int Sum(string property) => window.Sum(second => second.GetProperty(property).GetInt32());
        return new Totals(Sum("completed"), Sum("ok"), Sum("refused"), Sum("failed") + Sum("clientErrors"));
    }

    public sealed record Totals(int Completed, int Ok, int Refused, int Failed);

    // The cluster (D31, D39, D45): what the Scaling Lab and `lab flag` change

    private static Kubernetes Kubernetes() => new(KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: "kind-eventticketing"));

    public static async Task<(int Wanted, int Ready)> PublicApiAsync()
    {
        using var kubernetes = Kubernetes();
        var deployment = await kubernetes.AppsV1.ReadNamespacedDeploymentAsync("publicapi", Product, cancellationToken: Ct);
        return (deployment.Spec.Replicas ?? 0, deployment.Status.ReadyReplicas ?? 0);
    }

    public static async Task ScalePublicApiAsync(int count)
    {
        using var kubernetes = Kubernetes();
        var patch = new V1Patch("{\"spec\":{\"replicas\":" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}", V1Patch.PatchType.MergePatch);
        await kubernetes.AppsV1.PatchNamespacedDeploymentScaleAsync(patch, "publicapi", Product, cancellationToken: Ct);
    }

    /// <summary>What PublicApi's autoscaler measures and wants, for a test's message.</summary>
    public static async Task<string> AutoscalerAsync()
    {
        using var kubernetes = Kubernetes();
        try
        {
            var hpa = await kubernetes.AutoscalingV2.ReadNamespacedHorizontalPodAutoscalerAsync("keda-hpa-publicapi", Product, cancellationToken: Ct);
            var inProgress = hpa.Status?.CurrentMetrics?.FirstOrDefault(metric => metric.External is not null)?.External?.Current?.AverageValue;
            return $"autoscaler: {inProgress?.ToString() ?? "unknown"} requests in progress per instance, {hpa.Status?.CurrentReplicas} instances, wants {hpa.Status?.DesiredReplicas}";
        }
        catch (k8s.Autorest.HttpOperationException exception)
        {
            return $"autoscaler: {exception.Response.StatusCode}";
        }
    }

    /// <summary>The Auto switch (ADR-0054): off is KEDA's pause.</summary>
    public static async Task AutoAsync(bool on)
    {
        using var kubernetes = Kubernetes();
        var patch = new V1Patch(
            "{\"metadata\":{\"annotations\":{\"autoscaling.keda.sh/paused\":" + (on ? "null" : "\"true\"") + "}}}",
            V1Patch.PatchType.MergePatch);
        await kubernetes.CustomObjects.PatchNamespacedCustomObjectAsync(patch, "keda.sh", "v1alpha1", Product, "scaledobjects", "publicapi", cancellationToken: Ct);
    }

    /// <summary>A feature flag (FF1), as `lab flag` switches it: in the ConfigMap every instance re-reads.</summary>
    public static async Task FlagAsync(string flag, bool on)
    {
        using var kubernetes = Kubernetes();
        var map = await kubernetes.CoreV1.ReadNamespacedConfigMapAsync("eventticketing-features", Product, cancellationToken: Ct);
        var file = JsonNode.Parse(map.Data["features.json"])!;
        file["FeatureManagement"]![flag] = on;
        map.Data["features.json"] = file.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await kubernetes.CoreV1.ReplaceNamespacedConfigMapAsync(map, "eventticketing-features", Product, cancellationToken: Ct);
    }

    /// <summary>The lab as `lab up` leaves it: no load, Auto off, PublicApi at 3, every flag off.</summary>
    public static async Task RestoreAsync()
    {
        await LoadAsync(0);
        await AutoAsync(on: false);
        await ScalePublicApiAsync(3);
        await FlagAsync("PauseHolds", on: false);
    }

    // The lab script and the database, as an operator reaches them

    /// <summary>The repository's root, where lab.cs is.</summary>
    public static string RepositoryRoot { get; } = FindRoot();

    /// <summary>Runs `dotnet run lab.cs -- …` and returns its exit code and what it printed.</summary>
    public static Task<(int ExitCode, string Output)> LabAsync(params string[] arguments) =>
        RunAsync("dotnet", ["run", Path.Combine(RepositoryRoot, "lab.cs"), "--", .. arguments]);

    /// <summary>SQL as the database's owner on the primary, through kubectl, as `lab check` reads it.</summary>
    public static async Task<string> SqlAsync(string sql)
    {
        var kubectl = Path.Combine(RepositoryRoot, ".tools", "bin", OperatingSystem.IsWindows() ? "kubectl.exe" : "kubectl");
        var (_, primary) = await RunAsync(kubectl, ["--context", "kind-eventticketing", "get", "pods", "-n", Product, "-l", "cnpg.io/cluster=eventticketing,cnpg.io/instanceRole=primary", "-o", "jsonpath={.items[0].metadata.name}"]);
        var (exitCode, output) = await RunAsync(kubectl, ["--context", "kind-eventticketing", "exec", "-n", Product, primary.Trim(), "-c", "postgres", "--", "psql", "-U", "postgres", "-d", "eventticketing", "-v", "ON_ERROR_STOP=1", "-q", "-A", "-t", "-c", sql]);
        exitCode.Should().Be(0, output);
        return output.Trim();
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = RepositoryRoot };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await output + await errors);
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "lab.cs")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("lab.cs isn't above the test's folder.");
    }

    public static async Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object body) =>
        await client.PostAsync(new Uri(path, UriKind.Relative), new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), Ct);

    public static async Task<JsonElement> CreatedAsync(this HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
