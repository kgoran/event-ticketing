#:property PublishAot=false

// The lab environment (ADR-0022, M2 plan): a local Kubernetes cluster built from this repository.
//
//   dotnet run lab.cs -- up       create the cluster if there is none, then build and deploy the current source
//   dotnet run lab.cs -- reset    keep the cluster and platform; a fresh database, redeployed, reseeded
//   dotnet run lab.cs -- status   what runs, and where to open it
//   dotnet run lab.cs -- check    does it work: hosts ready, addresses answering, sign-in, a burst of load
//   dotnet run lab.cs -- deploy   roll the product out from the current source; --load 100 measures T10 under load
//   dotnet run lab.cs -- flag     the feature flags; `flag PauseHolds on` switches one in the running system
//   dotnet run lab.cs -- drill    a failure under load: `drill failover` (the primary), `drill zone` (a node)
//   dotnet run lab.cs -- down     delete the cluster and everything in it
//
// A .NET file-based app (D24), so it needs only the .NET SDK and Docker. The tools it drives
// (kind, kubectl, Helm) are pinned, downloaded into .tools/ and checked against their checksums;
// nothing is installed on the machine. Every command names the lab's own kubectl context, so other
// clusters are never touched.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var lab = new Lab(Lab.FindRoot());
try
{
    return (args.FirstOrDefault() ?? "help") switch
    {
        "up" => await lab.UpAsync(),
        "down" => lab.Down(),
        "reset" => await lab.ResetAsync(),
        "status" => lab.Status(),
        "check" => await lab.CheckAsync(args[1..]),
        "deploy" => await lab.DeployAsync(args[1..]),
        "flag" => lab.Flag(args[1..]),
        "drill" => await lab.DrillAsync(args[1..]),
        "backup" => await lab.BackupAsync(),
        "restore-check" => await lab.RestoreCheckAsync(),
        _ => Lab.Help(),
    };
}
catch (LabException exception)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"lab: {exception.Message}");
    return 1;
}

/// <summary>A failure the lab can explain; anything else is a bug and keeps its stack trace.</summary>
sealed class LabException(string message) : Exception(message);

sealed class Lab(string root)
{
    private const string ClusterName = "eventticketing";
    private const string Context = "kind-" + ClusterName;

    /// <summary>The usual localhost ports (ADR-0053): the cluster maps each one to Traefik.</summary>
    private static readonly (int Port, string What)[] Addresses =
    [
        (5100, "fan site"), (5101, "PublicApi"), (5102, "BackOfficeApi"), (5103, "WebhookIngress"), (5104, "CheckInApi"),
        (5201, "LocalPay"), (5300, "Scaling Lab"), (5301, "load generator"),
        (8025, "Mailpit"), (8180, "Keycloak"), (3000, "Grafana"), (8088, "Redpanda Console"),
    ];

    /// <summary>The images `up` builds, and the projects they come from.</summary>
    private static readonly (string Image, string Project)[] Images =
    [
        ("publicapi", "product/src/Hosts/PublicApi"),
        ("backofficeapi", "product/src/Hosts/BackOfficeApi"),
        ("webhookingress", "product/src/Hosts/WebhookIngress"), ("checkinapi", "product/src/Hosts/CheckInApi"),
        ("workers", "product/src/Hosts/Workers"),
        ("migrator", "product/src/Hosts/Migrator"),
        ("fansite", "product/src/Frontends/FanSite"),
        ("localpay", "tooling/simulators/LocalPay"),
        ("loadgenerator", "tooling/lab/LoadGenerator"),
        ("scalinglab", "tooling/lab/ScalingLab"),
    ];

    private readonly Tools _tools = new(Path.Combine(root, ".tools", "bin"));
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public static string FindRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "lab.cs")) && Directory.Exists(Path.Combine(directory.FullName, "platform")))
            {
                return directory.FullName;
            }
        }

        throw new LabException("Run this from the repository: dotnet run lab.cs -- up");
    }

    public static int Help()
    {
        Console.WriteLine("""
            The lab: the system in a local Kubernetes cluster (docs/milestones/m2.md).

              dotnet run lab.cs -- up       create the cluster if there is none, then build and deploy the current source
              dotnet run lab.cs -- reset    keep the cluster and platform; a fresh database, redeployed, reseeded
              dotnet run lab.cs -- status   what runs, and where to open it
              dotnet run lab.cs -- check    does it work: hosts ready, addresses answering, sign-in, a burst of load
              dotnet run lab.cs -- deploy   roll the product out from the current source, as a release would
                  --load <requests/s>       with the load generator running, and say how many requests failed (T10)
                  --restart                 roll every host out again even if nothing changed
                  --during-on-sale "<why>"  deploy although an on-sale's rush is on (the gate refuses otherwise)
              dotnet run lab.cs -- flag     the feature flags, and whether each is on
              dotnet run lab.cs -- flag <name> on|off   switch one in the running system, without a deploy
              dotnet run lab.cs -- drill failover       the database's primary deleted under load; T10 and T2 checked
              dotnet run lab.cs -- drill zone [zone-b|zone-c]   a zone's node stopped under load
              dotnet run lab.cs -- backup          a base backup of the database now, to SeaweedFS (D65)
              dotnet run lab.cs -- restore-check   the latest backup and the WAL restored into a new cluster, and compared
              dotnet run lab.cs -- down     delete the cluster and everything in it
            """);
        return 0;
    }

    public async Task<int> UpAsync()
    {
        Step("Docker");
        Capture("docker", ["info", "--format", "{{.ServerVersion}}"], failure: "Docker isn't running. Start Docker Desktop and try again.");

        Step("Tools");
        await _tools.EnsureAsync();

        if (ClusterExists() && ClusterNodes() < ConfiguredNodes())
        {
            throw new LabException(
                $"The lab cluster has {ClusterNodes()} node(s); this version of the lab makes {ConfiguredNodes()}, one per zone (D47). "
                + "Make it again: dotnet run lab.cs -- down, then up.");
        }

        // A cluster made before M4 doesn't map Redpanda Console's port: everything else works.
        if (ClusterExists() && !Capture("docker", ["port", $"{ClusterName}-control-plane"]).Contains(":8088", StringComparison.Ordinal))
        {
            Console.WriteLine("Note: this cluster was made before M4 and doesn't map port 8088 (Redpanda Console). Everything else works; down, then up, to get it.");
        }

        if (!ClusterExists())
        {
            Step("Cluster");
            var busy = Addresses.Where(address => InUse(address.Port)).ToList();
            if (busy.Count > 0)
            {
                throw new LabException(
                    $"Port {string.Join(", ", busy.Select(b => $"{b.Port} ({b.What})"))} is in use. The lab uses Aspire's ports (ADR-0053): stop the AppHost first.");
            }

            Run(_tools.Kind, ["create", "cluster", "--name", ClusterName, "--config", PathOf("platform/kind/cluster.yaml"), "--wait", "120s"]);
        }

        await InstallPlatformAsync();

        var tag = ImageTag();
        BuildAndLoadImages(tag);
        InstallProduct(tag);
        InstallTooling(tag);
        await SeedAsync();

        Step("Ready");
        PrintAddresses();
        return 0;
    }

    /// <summary>
    /// The fast path (ADR-0022): the cluster, platform and images stay; the product and tooling are
    /// removed with their data, then deployed and seeded again.
    /// </summary>
    public async Task<int> ResetAsync()
    {
        if (!_tools.Installed || !ClusterExists())
        {
            throw new LabException("No lab cluster. Start one with: dotnet run lab.cs -- up");
        }

        Step("Removing the product, its data and the tooling");
        foreach (var (release, space) in new[] { ("eventticketing-tooling", "lab"), ("eventticketing", "eventticketing"), ("eventticketing-database", "eventticketing") })
        {
            Run(_tools.Helm, ["--kube-context", Context, "uninstall", release, "--namespace", space, "--ignore-not-found", "--wait"]);
        }

        Kubectl("delete", "pvc", "-n", "eventticketing", "-l", "cnpg.io/cluster=eventticketing", "--ignore-not-found", "--wait");

        // The backups go with the database they saved (D65): a new database won't archive its WAL
        // where another one's already is.
        Kubectl("delete", "backups.postgresql.cnpg.io", "--all", "-n", "eventticketing", "--ignore-not-found");
        Kubectl("delete", "cluster", RestoreCluster, "-n", "eventticketing", "--ignore-not-found", "--wait");
        Weed("s3.bucket.delete -name backups");
        Weed("s3.bucket.create -name backups");

        // A restarted Toxiproxy forgets every toxic: the database isn't left slow (D57).
        Kubectl("rollout", "restart", "deployment/toxiproxy", "-n", "platform");
        Kubectl("rollout", "status", "deployment/toxiproxy", "-n", "platform", "--timeout=120s");

        var tag = ImageTag();
        BuildAndLoadImages(tag);
        InstallProduct(tag);
        InstallTooling(tag);
        await SeedAsync();

        Step("Ready");
        PrintAddresses();
        return 0;
    }

    /// <summary>What the images are built from: a change anywhere else (charts, docs, this script) rebuilds nothing.</summary>
    private static readonly string[] ImageSources =
    [
        "product/src", "tooling/lab", "tooling/simulators", "tooling/clients",
        "global.json", "Directory.Build.props", "Directory.Packages.props", "product/Directory.Build.props", "tooling/Directory.Build.props",
    ];

    /// <summary>
    /// The images' tag in the lab (ADR-0029 update, D25): a hash of the sources they are built from,
    /// committed or not. The same sources always get the same tag, so a repeated `up` rebuilds and
    /// restarts nothing, and changing only a chart rebuilds no image.
    /// </summary>
    private string ImageTag()
    {
        // Every file git would see, in a scratch index: the tree's entries are content hashes.
        var index = Path.Combine(Path.GetTempPath(), $"lab-index-{Environment.ProcessId}");
        try
        {
            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
            Capture("git", ["-C", root, "add", "--all"], environment: environment);
            var tree = Capture("git", ["-C", root, "write-tree"], environment: environment);
            var sources = string.Join('\n', ImageSources.Select(path => Capture("git", ["-C", root, "rev-parse", $"{tree}:{path}"])));
            return "src-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sources)))[..12];
        }
        finally
        {
            File.Delete(index);
        }
    }

    /// <summary>One image per host (ADR-0029), built by the SDK from the current source and loaded into the cluster.</summary>
    private void BuildAndLoadImages(string tag)
    {
        Step($"Images: {tag}");
        foreach (var (image, project) in Images)
        {
            if (Succeeds("docker", ["image", "inspect", $"eventticketing/{image}:{tag}"]))
            {
                Console.WriteLine($"  eventticketing/{image}:{tag} is built already");
                continue;
            }

            Run("dotnet", ["publish", PathOf(project), "-c", "Release", "-t:PublishContainer", $"-p:ContainerImageTag={tag}", "-v:quiet", "-nologo"]);
        }

        // Only what a node lacks: kind exports every image it is given before it looks. Every node,
        // since a pod may start on any of them (D47).
        Step("Images: into the cluster");
        var nodes = Capture(_tools.Kind, ["get", "nodes", "--name", ClusterName]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var present = nodes.Select(node => Capture("docker", ["exec", node, "crictl", "images", "-o", "json"])).ToList();
        var missing = Images.Select(pair => $"eventticketing/{pair.Image}:{tag}")
            .Where(image => present.Any(images => !images.Contains($"\"docker.io/{image}\"", StringComparison.Ordinal)))
            .ToList();
        if (missing.Count == 0)
        {
            Console.WriteLine("  The cluster has them all.");
            return;
        }

        Run(_tools.Kind, ["load", "docker-image", "--name", ClusterName, .. missing]);
    }

    /// <summary>
    /// The database first, as its own release, then the product: the Migrator runs before the hosts
    /// roll out (D32), and Helm waits until every host is ready.
    /// </summary>
    private void InstallProduct(string tag)
    {
        Step("Product: database");
        string[] spec = ["--context", Context, "get", "cluster", "eventticketing", "-n", "eventticketing", "-o", "jsonpath={.spec}", "--ignore-not-found"];
        var before = Capture(_tools.Kubectl, spec);
        Helm("eventticketing-database", PathOf("product/deploy/database"), repo: null, version: null, "eventticketing", PathOf("product/deploy/database/values-lab.yaml"));

        // A changed cluster may restart its instances one by one (BK1's plugin did), but Ready stays
        // True until the operator gets to it, a few seconds later: wait for it to start first.
        if (before.Length > 0 && before != Capture(_tools.Kubectl, spec))
        {
            var changed = Stopwatch.StartNew();
            while (changed.Elapsed < TimeSpan.FromSeconds(30)
                && Capture(_tools.Kubectl, ["--context", Context, "get", "cluster", "eventticketing", "-n", "eventticketing", "-o", "jsonpath={.status.phase}"]) == "Cluster in healthy state")
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        Kubectl("wait", "--for=condition=Ready", "cluster/eventticketing", "-n", "eventticketing", "--timeout=600s");

        DeployProduct(tag);
    }

    /// <summary>The product's release: the Migrator first, then each host rolls out what changed (D32, DP2).</summary>
    private void DeployProduct(string tag)
    {
        Step("Product: migrations and hosts");
        Helm("eventticketing", PathOf("product/deploy/chart"), repo: null, version: null, "eventticketing",
            PathOf("product/deploy/chart/values-lab.yaml"), $"image.tag={tag}");
        Kubectl("get", "deployments", "-n", "eventticketing");
    }

    private static readonly Uri GeneratorSettings = new("http://localhost:5301/settings");

    /// <summary>
    /// A release (DP2): the current source's images rolled out to the product while it serves. With
    /// --load, the load generator runs at that rate before, during and after the rollout, and the
    /// requests that failed are counted against T10: under 0.1%. The tooling isn't redeployed, so
    /// the generator keeps measuring (`up` updates it); the database stays as it is. First of all,
    /// the deploy gate (DP4): no release while an on-sale's rush is on.
    /// </summary>
    public async Task<int> DeployAsync(string[] options)
    {
        if (!_tools.Installed || !ClusterExists())
        {
            throw new LabException("No lab cluster. Start one with: dotnet run lab.cs -- up");
        }

        var (load, restart, emergency) = DeployOptions(options);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        Step("Deploy gate: no release during an on-sale (DP4)");
        if (await OnSaleRushAsync(http, DateTimeOffset.UtcNow) is not { } rush)
        {
            Console.WriteLine("  No on-sale opens around now.");
        }
        else if (emergency is null)
        {
            Console.WriteLine($"  Refused: {rush}.");
            Console.WriteLine("  Wait until then, or, if it can't wait: deploy --during-on-sale \"<why>\".");
            return 2;
        }
        else
        {
            Console.WriteLine($"  {rush}. Deploying anyway: \"{emergency}\".");
        }

        var tag = ImageTag();
        BuildAndLoadImages(tag);

        string? previous = null;
        if (load > 0)
        {
            Step($"Load: {load} requests/s through the gateway, 20% holds");
            previous = await http.GetStringAsync(GeneratorSettings);
            await SetLoadAsync(http, $"{{\"requestsPerSecond\":{load.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"holdShare\":0.2}}");
            await Task.Delay(TimeSpan.FromSeconds(10)); // steady before anything moves
        }

        var rollout = Stopwatch.StartNew();
        try
        {
            DeployProduct(tag);
            if (restart)
            {
                Step("Product: every host again, with the same images");
                Kubectl("rollout", "restart", "deployment", "-n", "eventticketing", "-l", "app.kubernetes.io/part-of=eventticketing");
                foreach (var deployment in Capture(_tools.Kubectl, ["--context", Context, "get", "deployments", "-n", "eventticketing", "-l", "app.kubernetes.io/part-of=eventticketing", "-o", "name"]).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    Kubectl("rollout", "status", deployment.Trim(), "-n", "eventticketing", "--timeout=300s");
                }
            }

            if (load > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(10)); // the last old instances finish their requests
            }
        }
        finally
        {
            if (previous is not null)
            {
                await SetLoadAsync(http, previous);
            }
        }

        if (load <= 0)
        {
            Step("Deployed");
            return 0;
        }

        // T10 (ADR-0015): a rolling deploy under load causes under 0.1% errors.
        var seconds = (int)Math.Ceiling(rollout.Elapsed.TotalSeconds);
        using var stats = JsonDocument.Parse(await http.GetStringAsync(new Uri($"http://localhost:5301/stats?seconds={seconds}")));
        var window = stats.RootElement.GetProperty("seconds").EnumerateArray().ToList();
        int Sum(string property) => window.Sum(second => second.GetProperty(property).GetInt32());
        var completed = Sum("completed");
        var failed = Sum("failed") + Sum("clientErrors");
        var share = completed == 0 ? 1 : failed / (double)completed;

        Step("T10: requests that failed during the rollout");
        Console.WriteLine($"  {completed} answered in {seconds} s, {failed} failed ({share:P3}), {Sum("skipped")} skipped. T10 allows under 0.1%.");
        if (failed > 0)
        {
            Console.WriteLine($"  Failed: {FailedByKind(window)}.");
        }
        if (seconds > 300)
        {
            Console.WriteLine("  The generator keeps 300 seconds; only the last 300 are counted.");
        }

        Console.WriteLine(share < 0.001 ? "  Within T10." : "  Over T10: see the failed requests' traces in Grafana (Explore, Tempo).");
        return share < 0.001 ? 0 : 1;
    }

    private static (double Load, bool Restart, string? Emergency) DeployOptions(string[] options)
    {
        double load = 0;
        var restart = false;
        string? emergency = null;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--load" when i + 1 < options.Length && double.TryParse(options[i + 1], System.Globalization.CultureInfo.InvariantCulture, out load) && load is > 0 and <= 1000:
                    i++;
                    break;
                case "--restart":
                    restart = true;
                    break;
                case "--during-on-sale" when i + 1 < options.Length && !string.IsNullOrWhiteSpace(options[i + 1]):
                    emergency = options[++i];
                    break;
                default:
                    throw new LabException($"deploy doesn't know '{options[i]}'. Options: --load <requests/s, 1 to 1000>, --restart, --during-on-sale \"<why>\".");
            }
        }

        return (load, restart, emergency);
    }

    /// <summary>
    /// The on-sale rush around which no release may start (DP4, D44): from 30 minutes before an
    /// on-sale opens until an hour after. Not the whole time an event is on sale, which can be months.
    /// </summary>
    private static readonly (TimeSpan Before, TimeSpan After) Rush = (TimeSpan.FromMinutes(30), TimeSpan.FromHours(1));

    /// <summary>
    /// Whether an on-sale's rush is on (ADR-0016): the schedule is Catalog's, read through PublicApi
    /// as anyone may, from every published event's opening time. Says which, and until when.
    /// </summary>
    private static async Task<string?> OnSaleRushAsync(HttpClient http, DateTimeOffset now)
    {
        var rushes = new List<(string Name, DateTimeOffset Opens)>();
        for (var page = 1; ; page++)
        {
            using var list = JsonDocument.Parse(await http.GetStringAsync(new Uri($"http://localhost:5101/events?pageSize=50&page={page}")));
            foreach (var item in list.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("onSaleAt", out var at) && at.ValueKind == JsonValueKind.String
                    && at.GetDateTimeOffset() is var opens && now >= opens - Rush.Before && now < opens + Rush.After)
                {
                    rushes.Add((item.GetProperty("name").GetString() ?? "an event", opens));
                }
            }

            if (!list.RootElement.GetProperty("hasMore").GetBoolean())
            {
                break;
            }
        }

        if (rushes.Count == 0)
        {
            return null;
        }

        var last = rushes.MaxBy(rush => rush.Opens);
        var names = string.Join(", ", rushes.Select(rush => $"{rush.Name} (on sale {rush.Opens.ToUniversalTime():HH:mm} UTC)"));
        return $"{names}: no release until {(last.Opens + Rush.After).ToUniversalTime():HH:mm} UTC";
    }

    private static async Task SetLoadAsync(HttpClient http, string settings)
    {
        using var content = new StringContent(settings, Encoding.UTF8, "application/json");
        using var response = await http.PutAsync(GeneratorSettings, content);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>LocalPay, the load generator and the Scaling Lab, next to the product (ADR-0021).</summary>
    private void InstallTooling(string tag)
    {
        Step("Tooling: LocalPay, load generator, Scaling Lab");
        Helm("eventticketing-tooling", PathOf("tooling/deploy/chart"), repo: null, version: null, "lab", values: null, $"image.tag={tag}");
    }

    /// <summary>The demo data, through the APIs as always (ADR-0022), when nothing is on sale yet.</summary>
    private async Task SeedAsync()
    {
        Step("Demo data");
        using var http = new HttpClient();
        var events = await http.GetStringAsync(new Uri("http://localhost:5101/events?pageSize=1"));
        if (events.Contains("\"eventId\"", StringComparison.Ordinal))
        {
            Console.WriteLine("  Events are on sale already.");
            return;
        }

        // The seeder's defaults are the usual localhost ports, which the lab serves too (ADR-0053).
        Run("dotnet", ["run", "--project", PathOf("tooling/seed"), "-c", "Release"]);
    }

    public int Down()
    {
        if (!_tools.Installed || !ClusterExists())
        {
            Console.WriteLine("No lab cluster.");
            return 0;
        }

        Run(_tools.Kind, ["delete", "cluster", "--name", ClusterName]);
        return 0;
    }

    /// <summary>
    /// Whether the lab works, as CI and a presenter need to know (D34): every host ready at its count,
    /// every address answering, a token from Keycloak accepted by PublicApi, the demo on sale, and a
    /// few seconds of load through the gateway without a failed request.
    /// </summary>
    public async Task<int> CheckAsync(string[] options)
    {
        if (!_tools.Installed || !ClusterExists())
        {
            throw new LabException("No lab cluster. Start one with: dotnet run lab.cs -- up");
        }

        if (options is ["--invariants"])
        {
            return InvariantsHold() ? 0 : 1;
        }

        if (options.Length > 0)
        {
            throw new LabException("check takes no options but --invariants.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var failures = new List<string>();
        async Task Check(string what, Func<Task<string?>> probe)
        {
            string? problem;
            try
            {
                problem = await probe();
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or LabException or JsonException or KeyNotFoundException)
            {
                problem = exception.Message;
            }

            Console.WriteLine($"  {(problem is null ? "ok  " : "FAIL")}  {what}{(problem is null ? "" : $": {problem}")}");
            if (problem is not null)
            {
                failures.Add(what);
            }
        }

        Step("Hosts");
        using (var deployments = JsonDocument.Parse(Capture(_tools.Kubectl, ["--context", Context, "get", "deployments", "--all-namespaces", "-o", "json"])))
        {
            foreach (var deployment in deployments.RootElement.GetProperty("items").EnumerateArray())
            {
                var name = deployment.GetProperty("metadata").GetProperty("name").GetString()!;
                var wanted = deployment.GetProperty("spec").GetProperty("replicas").GetInt32();
                var ready = deployment.GetProperty("status").TryGetProperty("readyReplicas", out var count) ? count.GetInt32() : 0;
                await Check($"{name}: {ready} of {wanted} ready", () => Task.FromResult(ready == wanted ? null : "not all instances are ready"));
            }
        }

        Step("Addresses");
        // A port a cluster made before a milestone doesn't map (8088 before M4) is noted, not failed.
        var mapped = Capture("docker", ["port", $"{ClusterName}-control-plane"]);
        foreach (var (port, what) in Addresses)
        {
            if (!mapped.Contains($":{port}", StringComparison.Ordinal))
            {
                Console.WriteLine($"  --    {what} at http://localhost:{port}: this cluster doesn't map the port; down, then up, to get it");
                continue;
            }

            await Check($"{what} at http://localhost:{port}", async () =>
            {
                using var response = await http.GetAsync(new Uri($"http://localhost:{port}/"));
                return (int)response.StatusCode < 500 ? null : $"HTTP {(int)response.StatusCode}";
            });
        }

        Step("Sign-in and the demo");
        await Check("a token from Keycloak is accepted by PublicApi", async () =>
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password", ["client_id"] = "eventticketing-dev", ["username"] = "ana", ["password"] = "ana",
            });
            using var token = await http.PostAsync(new Uri("http://localhost:8180/realms/eventticketing/protocol/openid-connect/token"), form);
            token.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
            using var me = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost:5101/me"));
            me.Headers.Authorization = new("Bearer", body.RootElement.GetProperty("access_token").GetString());
            using var answer = await http.SendAsync(me);
            return answer.IsSuccessStatusCode ? null : $"GET /me answered {(int)answer.StatusCode}";
        });
        await Check("the demo is on sale", async () =>
            (await http.GetStringAsync(new Uri("http://localhost:5101/events?q=demo"))).Contains("Demo Concert", StringComparison.Ordinal) ? null : "no Demo Concert");

        Step("A burst of load through the gateway");
        await Check("20 requests/s for 8 s, none failed", async () =>
        {
            static StringContent Rate(int rate) => new($"{{\"requestsPerSecond\":{rate},\"holdShare\":0.2}}", Encoding.UTF8, "application/json");
            (await http.PutAsync(new Uri("http://localhost:5301/settings"), Rate(20))).EnsureSuccessStatusCode();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(9));
                using var stats = JsonDocument.Parse(await http.GetStringAsync(new Uri("http://localhost:5301/stats?seconds=6")));
                var seconds = stats.RootElement.GetProperty("seconds").EnumerateArray().ToList();
                int Sum(string property) => seconds.Sum(second => second.GetProperty(property).GetInt32());
                return Sum("ok") == 0 ? "nothing was answered"
                    : Sum("failed") + Sum("clientErrors") > 0 ? $"{Sum("failed")} failed, {Sum("clientErrors")} client errors of {Sum("completed")}"
                    : null;
            }
            finally
            {
                await http.PutAsync(new Uri("http://localhost:5301/settings"), Rate(0));
            }
        });

        // Through Grafana's data source proxy, as anyone looking would see it (D55).
        Step("Telemetry, through the collector");
        async Task<JsonDocument> Grafana(string path) => JsonDocument.Parse(await http.GetStringAsync(new Uri($"http://localhost:3000/{path}")));
        await Check("PublicApi's metrics are in Prometheus", async () =>
        {
            using var answer = await Grafana("api/datasources/proxy/uid/prometheus/api/v1/query?query=" +
                Uri.EscapeDataString("sum(rate(http_server_request_duration_seconds_count{job=\"publicapi\"}[1m]))"));
            return answer.RootElement.GetProperty("data").GetProperty("result").GetArrayLength() > 0 ? null : "no request metrics from publicapi";
        });
        await Check("its traces are in Tempo", async () =>
        {
            using var answer = await Grafana("api/datasources/proxy/uid/tempo/api/search?limit=1&tags=" + Uri.EscapeDataString("service.name=publicapi"));
            return answer.RootElement.GetProperty("traces").GetArrayLength() > 0 ? null : "no traces from publicapi";
        });
        await Check("its logs are in Loki", async () =>
        {
            using var answer = await Grafana("api/datasources/proxy/uid/loki/loki/api/v1/query_range?limit=1&query=" + Uri.EscapeDataString("{service_name=\"publicapi\"}"));
            return answer.RootElement.GetProperty("data").GetProperty("result").GetArrayLength() > 0 ? null : "no log lines from publicapi";
        });
        await Check("the product's dashboards are in Grafana", async () =>
        {
            using var answer = await Grafana("api/search?tag=eventticketing&type=dash-db");
            var titles = answer.RootElement.EnumerateArray().Select(dashboard => dashboard.GetProperty("title").GetString()).ToList();
            return titles.Count >= 3 ? null : $"found {titles.Count}: {string.Join(", ", titles)}";
        });

        Step("Kafka (D76)");
        await Check("three Redpanda brokers, healthy", () =>
        {
            var health = Capture(_tools.Kubectl, ["--context", Context, "exec", "-n", "platform", "redpanda-0", "-c", "redpanda", "--", "rpk", "cluster", "health"]);
            var healthy = health.Split('\n').Any(line => line.StartsWith("Healthy:", StringComparison.Ordinal) && line.TrimEnd().EndsWith("true", StringComparison.Ordinal));
            var nodes = health.Split('\n').FirstOrDefault(line => line.StartsWith("All nodes:", StringComparison.Ordinal))?.Split('[', ']')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
            return Task.FromResult(healthy && nodes == 3 ? null : $"healthy: {healthy}, brokers: {nodes}");
        });

        Step("The database's backups (D65)");
        await Check("its WAL is archived to SeaweedFS", () =>
        {
            var archiving = Capture(_tools.Kubectl, ["--context", Context, "get", "cluster", "eventticketing", "-n", "eventticketing", "-o", "jsonpath={.status.conditions[?(@.type==\"ContinuousArchiving\")]}"]);
            if (string.IsNullOrEmpty(archiving))
            {
                return Task.FromResult<string?>("not yet: CloudNativePG hasn't reported on it");
            }

            using var condition = JsonDocument.Parse(archiving);
            return Task.FromResult(condition.RootElement.GetProperty("status").GetString() == "True" ? null : condition.RootElement.GetProperty("message").GetString());
        });

        Step("The database, through Toxiproxy");
        await Check("nothing slows it (D57)", () =>
        {
            using var toxics = JsonDocument.Parse(Capture(_tools.Kubectl, ["--context", Context, "get", "--raw", "/api/v1/namespaces/platform/services/toxiproxy:8474/proxy/proxies/database/toxics"]));
            var count = toxics.RootElement.GetArrayLength();
            return Task.FromResult(count == 0 ? null : $"{count} toxic(s) on it: Stop in the Scaling Lab, or dotnet run lab.cs -- reset");
        });

        if (!InvariantsHold())
        {
            failures.Add("T2's invariants");
        }

        Console.WriteLine();
        Console.WriteLine(failures.Count == 0 ? "The lab works." : $"{failures.Count} check(s) failed.");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// T2's invariants (D58): tooling/checks/invariants.sql in one read-only transaction, so all the
    /// modules' schemas are read at the same moment and nothing is written; on a replica, since a
    /// check decides nothing (the primary if no replica is ready). Each violation is printed.
    /// </summary>
    private bool InvariantsHold()
    {
        Step("T2's invariants (tooling/checks)");
        var pod = DatabasePod("replica") ?? DatabasePod("primary") ?? throw new LabException("No database instance is running.");
        var sql = "BEGIN TRANSACTION READ ONLY;\n" + File.ReadAllText(PathOf("tooling/checks/invariants.sql")) + "\nCOMMIT;\n";
        var violations = Capture(
                _tools.Kubectl,
                ["--context", Context, "exec", "-i", "-n", "eventticketing", pod, "-c", "postgres", "--",
                 "psql", "-U", "postgres", "-d", "eventticketing", "-v", "ON_ERROR_STOP=1", "-q", "-A", "-t", "-F", " | "],
                stdin: sql)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line is not ("BEGIN" or "COMMIT"))
            .ToList();

        Console.WriteLine(violations.Count == 0 ? $"  ok    every rule holds (read on {pod})" : $"  FAIL  {violations.Count} violation(s), read on {pod}:");
        foreach (var violation in violations)
        {
            Console.WriteLine($"        {violation}");
        }

        return violations.Count == 0;
    }

    /// <summary>
    /// The drills (D59, T10): a failure under constant load, timed from outside. `failover` deletes
    /// the database's primary; `zone` stops a worker node (zone-b or zone-c: zone-a is the lab's
    /// way in, and holds the gateway, the identity provider and the tooling, as the outside world
    /// would). Prints when the failure began, when the database had a new primary, when requests
    /// failed, and then checks T2's invariants. T10 allows under 60 s of errors for a failover.
    /// </summary>
    public async Task<int> DrillAsync(string[] options)
    {
        if (!_tools.Installed || !ClusterExists())
        {
            throw new LabException("No lab cluster. Start one with: dotnet run lab.cs -- up");
        }

        var primary = DatabasePod("primary") ?? throw new LabException("No database primary is running.");
        var primaryNode = Capture(_tools.Kubectl, ["--context", Context, "get", "pod", primary, "-n", "eventticketing", "-o", "jsonpath={.spec.nodeName}"]);
        string[] workers = [$"{ClusterName}-worker", $"{ClusterName}-worker2"];

        // Without a zone named, the primary's, if a drill may stop it: the harder case.
        var (kind, node) = options switch
        {
            ["failover"] => ("failover", null),
            ["zone"] => ("zone", workers.Contains(primaryNode) ? primaryNode : workers[0]),
            ["zone", "zone-b"] => ("zone", workers[0]),
            ["zone", "zone-c"] => ("zone", workers[1]),
            _ => throw new LabException("Use: drill failover, or drill zone [zone-b|zone-c]."),
        };
        var promotes = kind == "failover" || node == primaryNode;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var previous = await http.GetStringAsync(GeneratorSettings);

        // A topic with three replicas, to write to while a zone is down (D76); made while all three run.
        if (kind == "zone")
        {
            Succeeds(_tools.Kubectl, ["--context", Context, "exec", "-n", "platform", "redpanda-0", "-c", "redpanda", "--", "rpk", "topic", "create", "lab.drill", "-r", "3", "-p", "1"]);
        }

        Step("Load: 100 requests/s through the gateway, 20% holds");
        await SetLoadAsync(http, """{"requestsPerSecond":100,"holdShare":0.2}""");
        await Task.Delay(TimeSpan.FromSeconds(15)); // steady before anything breaks

        var clock = Stopwatch.StartNew();
        TimeSpan? promoted = null;
        try
        {
            if (kind == "failover")
            {
                // A crash, not a shutdown: no time to finish anything. A graceful delete waits for
                // PostgreSQL's smart shutdown, which waits for the pools' connections to close.
                Step($"Failover: the primary, {primary}, crashes");
                Kubectl("delete", "pod", primary, "-n", "eventticketing", "--grace-period=0", "--force", "--wait=false");
            }
            else
            {
                Step($"Zone: the node {node} stops{(promotes ? ", with the database's primary" : ", not the primary's")}");
                Run("docker", ["stop", node!]);
            }

            // Until the database has a primary again (CloudNativePG promotes a standby), then a minute
            // more under load, so the drill shows the way back as well as the break. Without the
            // primary in the zone, two minutes: long enough for Kubernetes to notice and move the hosts.
            while (clock.Elapsed < TimeSpan.FromMinutes(5))
            {
                if (promoted is null && DatabasePod("primary") is { } now && now != primary)
                {
                    promoted = clock.Elapsed;
                    Console.WriteLine($"  {now} is the primary after {promoted.Value.TotalSeconds:0} s");
                }

                if ((promoted is { } at && clock.Elapsed > at + TimeSpan.FromSeconds(60)) || (!promotes && clock.Elapsed > TimeSpan.FromMinutes(2)))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            await SetLoadAsync(http, previous);
        }

        // Second by second, from the generator: which seconds failed requests.
        var seconds = (int)Math.Ceiling(clock.Elapsed.TotalSeconds);
        using var stats = JsonDocument.Parse(await http.GetStringAsync(new Uri($"http://localhost:5301/stats?seconds={Math.Min(seconds, 300)}")));
        var window = stats.RootElement.GetProperty("seconds").EnumerateArray().ToList();
        var failing = window.Select((second, index) => (index, failed: second.GetProperty("failed").GetInt32() + second.GetProperty("clientErrors").GetInt32()))
            .Where(second => second.failed > 0).ToList();
        var completed = window.Sum(second => second.GetProperty("completed").GetInt32());
        var errorSeconds = failing.Count == 0 ? 0 : failing[^1].index - failing[0].index + 1;

        Step("T10: requests that failed during the drill");
        Console.WriteLine(promoted is { } p ? $"  A new primary after {p.TotalSeconds:0} s."
            : promotes ? "  No new primary within 5 minutes." : $"  The primary, {primary}, kept running in another zone.");
        Console.WriteLine(failing.Count == 0
            ? $"  {completed} answered, none failed."
            : $"  {completed} answered, {failing.Sum(second => second.failed)} failed, within {errorSeconds} s (from {failing[0].index - (window.Count - seconds)} s to {failing[^1].index - (window.Count - seconds)} s after the failure).");
        if (failing.Count > 0)
        {
            Console.WriteLine($"  Failed: {FailedByKind(window)}.");
        }

        Console.WriteLine(kind == "failover"
            ? (errorSeconds < 60 ? "  Within T10 (under 60 s of errors)." : "  Over T10: 60 s of errors or more.")
            : "  A zone may take minutes to recover (§10); its database instance waits for the node, the hosts move.");

        // Browsing reads the replica and the cache (RR1, VK1): without the primary it goes on (M3).
        var browsed = window.Sum(second => ByKind(second, "completedByKind", "Browse"));
        var browsingFailed = window.Sum(second => ByKind(second, "failedByKind", "Browse"));
        var browsingWentOn = browsingFailed <= browsed / 100;
        if (kind == "failover")
        {
            Console.WriteLine($"  Browsing: {browsingFailed} of {browsed} failed. {(browsingWentOn ? "It went on without the primary." : "Over 1%: it should go on without the primary (the replica and the cache).")}");
        }

        var invariants = InvariantsHold();
        var kafka = node is null || KafkaWritesWithout(node);
        if (node is not null)
        {
            Step($"Zone: the node {node} starts again");
            Run("docker", ["start", node]);
            Kubectl("wait", "node", node, "--for=condition=Ready", "--timeout=180s");
            Kubectl("wait", "--for=condition=Ready", "cluster/eventticketing", "-n", "eventticketing", "--timeout=300s");
        }
        return (promoted is not null || !promotes) && invariants && kafka && (kind != "failover" || (errorSeconds < 60 && browsingWentOn)) ? 0 : 1;
    }

    /// <summary>
    /// With a zone down, Kafka still takes writes (D76): a message produced with acks=all, through a
    /// broker that runs, needs a majority of its three replicas, and two remain.
    /// </summary>
    private bool KafkaWritesWithout(string node)
    {
        Step("Kafka: a write with acks=all while the zone is down");
        var broker = Capture(_tools.Kubectl, ["--context", Context, "get", "pods", "-n", "platform", "-l", "app.kubernetes.io/name=redpanda", "-o", $"jsonpath={{.items[?(@.spec.nodeName!=\"{node}\")].metadata.name}}"])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var written = broker is not null && Succeeds(
            _tools.Kubectl, ["--context", Context, "exec", "-i", "-n", "platform", broker, "-c", "redpanda", "--", "rpk", "topic", "produce", "lab.drill", "--acks", "-1"],
            stdin: $"written without {node}\n");
        Console.WriteLine(written ? $"  ok    two of three replicas took it, through {broker}" : "  FAIL  the write wasn't acknowledged");
        return written;
    }

    /// <summary>The load generator's failed requests per kind over its seconds, as a drill or a deploy reports them.</summary>
    private static string FailedByKind(IReadOnlyCollection<JsonElement> window) =>
        string.Join(", ", new[] { ("Hold", "holds"), ("Release", "releases"), ("Browse", "browsing") }
            .Select(kind => $"{kind.Item2} {window.Sum(second => ByKind(second, "failedByKind", kind.Item1))}"));

    private static int ByKind(JsonElement second, string property, string kind) =>
        second.TryGetProperty(property, out var byKind) && byKind.TryGetProperty(kind, out var count) ? count.GetInt32() : 0;

    private const string RestoreCluster = "eventticketing-restore";

    /// <summary>
    /// A base backup now (D65), as before a risky change: CloudNativePG has the Barman Cloud plugin
    /// copy an instance's data files to SeaweedFS (a standby's, if one is ready, to spare the
    /// primary). The WAL goes there continuously anyway; a base backup is where a restore starts.
    /// </summary>
    public async Task<int> BackupAsync()
    {
        if (!_tools.Installed || !ClusterExists())
        {
            throw new LabException("No lab cluster. Start one with: dotnet run lab.cs -- up");
        }

        var name = $"manual-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        Step($"Backup {name}");
        Apply($"""
            apiVersion: postgresql.cnpg.io/v1
            kind: Backup
            metadata:
              name: {name}
              namespace: eventticketing
            spec:
              cluster:
                name: eventticketing
              method: plugin
              pluginConfiguration:
                name: barman-cloud.cloudnative-pg.io
            """);

        var clock = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            using var backup = JsonDocument.Parse(Capture(_tools.Kubectl, ["--context", Context, "get", "backups.postgresql.cnpg.io", name, "-n", "eventticketing", "-o", "json"]));
            var status = backup.RootElement.TryGetProperty("status", out var value) ? value : default;
            var phase = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("phase", out var current) ? current.GetString() : null;
            if (phase == "completed")
            {
                Console.WriteLine($"  completed in {clock.Elapsed.TotalSeconds:0} s, from {status.GetProperty("instanceID").GetProperty("podName").GetString()}: "
                    + $"WAL {status.GetProperty("beginWal").GetString()} to {status.GetProperty("endWal").GetString()}");
                return 0;
            }

            if (phase == "failed" || clock.Elapsed > TimeSpan.FromMinutes(10))
            {
                throw new LabException($"The backup {name} {(phase == "failed" ? "failed" : "didn't finish in 10 minutes")}: "
                    + (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("error", out var error) ? error.GetString() : phase ?? "no status"));
            }
        }
    }

    /// <summary>
    /// The restore drill (D65): a backup counts once it has been restored. The orders and tickets
    /// are counted at a named point in the WAL; once that WAL is archived, the latest base backup
    /// and the WAL up to the point are restored into a new one-instance cluster, and counted
    /// again. The running database never notices. The new cluster is deleted if the counts match,
    /// and left to look at if they don't.
    /// </summary>
    public async Task<int> RestoreCheckAsync()
    {
        if (!_tools.Installed || !ClusterExists())
        {
            throw new LabException("No lab cluster. Start one with: dotnet run lab.cs -- up");
        }

        // The latest base backup: a point named in the WAL says nothing about time, so CloudNativePG
        // can't choose the backup before it, and is told.
        using var backups = JsonDocument.Parse(Capture(_tools.Kubectl, ["--context", Context, "get", "backups.postgresql.cnpg.io", "-n", "eventticketing", "-o", "json"]));
        var latest = backups.RootElement.GetProperty("items").EnumerateArray()
            .Where(backup => backup.TryGetProperty("status", out var status) && status.TryGetProperty("phase", out var phase) && phase.GetString() == "completed")
            .Select(backup => (Name: backup.GetProperty("metadata").GetProperty("name").GetString(), Id: backup.GetProperty("status").GetProperty("backupId").GetString(), Stopped: backup.GetProperty("status").GetProperty("stoppedAt").GetDateTimeOffset()))
            .OrderBy(backup => backup.Stopped)
            .LastOrDefault();
        if (latest.Id is null)
        {
            throw new LabException("No backup has completed yet. Take one with: dotnet run lab.cs -- backup");
        }

        Kubectl("delete", "cluster", RestoreCluster, "-n", "eventticketing", "--ignore-not-found", "--wait");
        var primary = DatabasePod("primary") ?? throw new LabException("No database primary is running.");

        // The counts and the point, at one moment: the SHARE lock lets no write to either table
        // commit between them (the writers wait the milliseconds it takes). Each table is written
        // in its module's own transactions, so nothing holds one while it waits for the other.
        var point = $"restore-check-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        Step($"The point {point}, on {primary}");
        const string Counts = "SELECT (SELECT count(*) FROM orders.orders), (SELECT count(*) FROM ticketing.tickets)";
        var marked = Sql(primary, $"""
            BEGIN;
            SET LOCAL lock_timeout = '5s';
            LOCK TABLE orders.orders, ticketing.tickets IN SHARE MODE;
            {Counts}, pg_create_restore_point('{point}');
            COMMIT;
            SELECT pg_walfile_name(pg_switch_wal());
            """).Where(line => line is not ("BEGIN" or "SET" or "LOCK TABLE" or "COMMIT")).ToList();
        var expected = string.Join('|', marked[0].Split('|')[..2]);
        var segment = marked[1];
        Console.WriteLine($"  orders | tickets: {expected.Replace("|", " | ")}");

        Step($"Waiting until the WAL segment {segment} is archived");
        var clock = Stopwatch.StartNew();
        while (string.CompareOrdinal(Sql(primary, "SELECT coalesce(last_archived_wal, '') FROM pg_stat_archiver;").FirstOrDefault() ?? "", segment) < 0)
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(2))
            {
                throw new LabException($"The WAL segment {segment} wasn't archived in 2 minutes: dotnet run lab.cs -- check says why.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Console.WriteLine($"  archived after {clock.Elapsed.TotalSeconds:0.0} s");

        // The same PostgreSQL and the same limits: a standby's max_connections may not be lower than
        // its primary's, and a restore replays the WAL as a standby does.
        Step($"Restoring {latest.Name} and the WAL up to the point into {RestoreCluster}");
        using var source = JsonDocument.Parse(Capture(_tools.Kubectl, ["--context", Context, "get", "cluster", "eventticketing", "-n", "eventticketing", "-o", "json"]));
        var spec = source.RootElement.GetProperty("spec");
        Apply($"""
            apiVersion: postgresql.cnpg.io/v1
            kind: Cluster
            metadata:
              name: {RestoreCluster}
              namespace: eventticketing
            spec:
              instances: 1
              imageName: {spec.GetProperty("imageName").GetString()}
              storage:
                size: {spec.GetProperty("storage").GetProperty("size").GetString()}
              postgresql:
                parameters:
                  max_connections: "{spec.GetProperty("postgresql").GetProperty("parameters").GetProperty("max_connections").GetString()}"
              resources:
                requests:
                  cpu: 100m
                  memory: 256Mi
                limits:
                  memory: 1Gi
              bootstrap:
                recovery:
                  source: backups
                  recoveryTarget:
                    backupID: {latest.Id}
                    targetName: {point}
              externalClusters:
                - name: backups
                  plugin:
                    name: barman-cloud.cloudnative-pg.io
                    parameters:
                      barmanObjectName: eventticketing-backups
                      serverName: eventticketing
            """);
        clock.Restart();
        Kubectl("wait", "--for=condition=Ready", $"cluster/{RestoreCluster}", "-n", "eventticketing", "--timeout=600s");
        Console.WriteLine($"  ready after {clock.Elapsed.TotalSeconds:0} s");

        var restored = Sql($"{RestoreCluster}-1", Counts + ";")[0];
        var same = restored == expected;
        Step(same ? "The same orders and tickets" : "Different");
        Console.WriteLine($"  at the point:  orders | tickets: {expected.Replace("|", " | ")}");
        Console.WriteLine($"  restored:      orders | tickets: {restored.Replace("|", " | ")}");
        if (!same)
        {
            Console.WriteLine($"  {RestoreCluster} is left to look at; reset or restore-check deletes it.");
            return 1;
        }

        Kubectl("delete", "cluster", RestoreCluster, "-n", "eventticketing", "--wait");
        return 0;
    }

    /// <summary>
    /// A healthy database instance in the given role (primary or replica), if there is one, as
    /// CloudNativePG sees them: a pod on a lost node can still look ready to the API for a while.
    /// </summary>
    private string? DatabasePod(string role)
    {
        using var cluster = JsonDocument.Parse(Capture(_tools.Kubectl, ["--context", Context, "get", "cluster", "eventticketing", "-n", "eventticketing", "-o", "json"]));
        var status = cluster.RootElement.GetProperty("status");
        var primary = status.TryGetProperty("currentPrimary", out var current) ? current.GetString() : null;
        var healthy = status.TryGetProperty("instancesStatus", out var instances) && instances.TryGetProperty("healthy", out var list)
            ? list.EnumerateArray().Select(name => name.GetString()!).ToList()
            : [];
        return role == "primary"
            ? healthy.Contains(primary ?? "") ? primary : null
            : healthy.FirstOrDefault(name => name != primary);
    }

    /// <summary>
    /// The feature flags (FF1, docs/feature-flags.md): each one and whether it is on, or one switched
    /// in the running system. Every instance re-reads the ConfigMap's file: Kubernetes updates the
    /// file within about a minute, and .NET notices within seconds. No deploy, no restart.
    /// </summary>
    public int Flag(string[] options)
    {
        if (!_tools.Installed || !ClusterExists())
        {
            throw new LabException("No lab cluster. Start one with: dotnet run lab.cs -- up");
        }

        var file = JsonNode.Parse(Capture(_tools.Kubectl, ["--context", Context, "get", "configmap", "eventticketing-features", "-n", "eventticketing", "-o", @"jsonpath={.data.features\.json}"]))!;
        var flags = file["FeatureManagement"]!.AsObject();
        if (options.Length == 0)
        {
            foreach (var (name, value) in flags)
            {
                Console.WriteLine($"  {name,-24} {(value?.GetValue<bool>() == true ? "on" : "off")}");
            }

            return 0;
        }

        if (options is not [var flag, "on" or "off"])
        {
            throw new LabException("Use: flag <name> on|off, or flag alone for the list.");
        }

        if (!flags.ContainsKey(flag))
        {
            throw new LabException($"There is no flag '{flag}'. The flags: {string.Join(", ", flags.Select(pair => pair.Key))}.");
        }

        flags[flag] = options[1] == "on";
        var patch = new JsonObject { ["data"] = new JsonObject { ["features.json"] = file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) } };
        Kubectl("patch", "configmap", "eventticketing-features", "-n", "eventticketing", "--type", "merge", "-p", patch.ToJsonString());
        Console.WriteLine($"{flag} is {options[1]}. Every instance reads it within about a minute, without a deploy or a restart.");
        return 0;
    }

    public int Status()
    {
        if (!_tools.Installed || !ClusterExists())
        {
            Console.WriteLine("No lab cluster. Start one with: dotnet run lab.cs -- up");
            return 0;
        }

        Run(_tools.Kubectl, ["--context", Context, "get", "nodes", "-L", "topology.kubernetes.io/zone"]);
        Console.WriteLine();
        Run(_tools.Kubectl, ["--context", Context, "get", "pods", "--all-namespaces", "-l", "!job-name", "-o", "wide"]);
        Console.WriteLine();
        // The database (D48): which instance is the primary, and where each one runs.
        Run(_tools.Kubectl, ["--context", Context, "get", "cluster", "eventticketing", "-n", "eventticketing"]);
        Run(_tools.Kubectl, ["--context", Context, "get", "pods", "-n", "eventticketing", "-l", "cnpg.io/cluster=eventticketing", "-L", "cnpg.io/instanceRole", "-o", "wide"]);
        Console.WriteLine();
        // Its backups (D65), the latest last.
        Run(_tools.Kubectl, ["--context", Context, "get", "backups.postgresql.cnpg.io", "-n", "eventticketing", "--sort-by=.metadata.creationTimestamp"]);
        Console.WriteLine();
        PrintAddresses();
        return 0;
    }

    private async Task InstallPlatformAsync()
    {
        Step("Platform: namespaces and Gateway API");
        Kubectl("apply", "-f", PathOf("platform/manifests/namespaces.yaml"));
        var gatewayApi = await _tools.DownloadVerifiedAsync(Pins.GatewayApiCrds, Pins.GatewayApiCrdsSha256, "gateway-api-standard-install.yaml");
        Kubectl("apply", "--server-side", "--force-conflicts", "-f", gatewayApi);

        Step("Platform: Traefik, CloudNativePG, metrics-server, KEDA");
        Helm("traefik", Chart("traefik", Pins.TraefikRepo, Pins.TraefikChart), repo: null, version: null, "platform", PathOf("platform/traefik/values.yaml"));
        Helm("cloudnative-pg", Chart("cloudnative-pg", Pins.CloudNativePgRepo, Pins.CloudNativePgChart), repo: null, version: null, "cnpg-system", PathOf("platform/cloudnative-pg/values.yaml"));
        Helm("metrics-server", Chart("metrics-server", Pins.MetricsServerRepo, Pins.MetricsServerChart), repo: null, version: null, "kube-system", PathOf("platform/metrics-server/values.yaml"));
        // Before the product: its chart has a ScaledObject for PublicApi's Auto switch (ADR-0054).
        Helm("keda", Chart("keda", Pins.KedaRepo, Pins.KedaChart), repo: null, version: null, "keda", PathOf("platform/keda/values.yaml"));
        Kubectl("wait", "--for=condition=Programmed", "gateway/lab", "-n", "platform", "--timeout=120s");

        Step("Platform: secrets");
        var secrets = EnsureSecrets();

        // Backups (D65): the Barman Cloud plugin talks to CloudNativePG's operator over TLS that
        // cert-manager issues, and sends them to SeaweedFS. Before the product: its database names both.
        Step("Platform: cert-manager, the Barman Cloud plugin, SeaweedFS");
        Helm("cert-manager", Chart("cert-manager", Pins.CertManagerRepo, Pins.CertManagerChart), repo: null, version: null, "cert-manager", PathOf("platform/cert-manager/values.yaml"));
        Helm("plugin-barman-cloud", Chart("plugin-barman-cloud", Pins.CloudNativePgRepo, Pins.BarmanCloudPluginChart), repo: null, version: null, "cnpg-system", PathOf("platform/plugin-barman-cloud/values.yaml"));
        var identities = new JsonObject
        {
            ["identities"] = new JsonArray(new JsonObject
            {
                ["name"] = "backups",
                ["credentials"] = new JsonArray(new JsonObject { ["accessKey"] = secrets["backup-access-key"], ["secretKey"] = secrets["backup-secret-key"] }),
                ["actions"] = new JsonArray("Admin", "Read", "Write", "List", "Tagging"),
            }),
        };
        Apply(Capture(_tools.Kubectl, ["--context", Context, "create", "secret", "generic", "seaweedfs-s3", "-n", "platform", $"--from-literal=s3.json={identities.ToJsonString()}", "--dry-run=client", "-o", "yaml"]));
        Kubectl("apply", "-f", PathOf("platform/manifests/seaweedfs.yaml"));
        Kubectl("rollout", "status", "deployment/seaweedfs", "-n", "platform", "--timeout=300s");
        if (!Weed("s3.bucket.list").Split('\n').Any(line => line.Trim().StartsWith("backups", StringComparison.Ordinal)))
        {
            Weed("s3.bucket.create -name backups");
        }

        // Kafka (D76): three brokers, one per zone, and Redpanda Console. Before the product: the
        // Migrator creates its topics.
        Step("Platform: Redpanda");
        Kubectl("apply", "-f", PathOf("platform/manifests/redpanda.yaml"));
        Kubectl("rollout", "status", "statefulset/redpanda", "-n", "platform", "--timeout=300s");
        Kubectl("rollout", "status", "deployment/redpanda-console", "-n", "platform", "--timeout=180s");

        Step("Platform: Keycloak, Mailpit, Toxiproxy");
        var realm = PathOf("dev/AppHost/Realms/eventticketing-realm.json");
        Apply(Capture(_tools.Kubectl, ["--context", Context, "create", "configmap", "keycloak-realm", "-n", "platform", $"--from-file={realm}", "--dry-run=client", "-o", "yaml"]));
        // Keycloak imports the realm only when it starts: a changed realm file restarts it.
        var realmHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(realm)))[..16];
        Apply(File.ReadAllText(PathOf("platform/manifests/keycloak.yaml")).Replace("REALM_HASH", realmHash, StringComparison.Ordinal));
        Kubectl("apply", "-f", PathOf("platform/manifests/mailpit.yaml"));
        // Between the hosts and PgBouncer (D57): it must run before the product does.
        Kubectl("apply", "-f", PathOf("platform/manifests/toxiproxy.yaml"));

        foreach (var deployment in new[] { "keycloak", "mailpit", "toxiproxy" })
        {
            Kubectl("rollout", "status", $"deployment/{deployment}", "-n", "platform", "--timeout=300s");
        }

        // Observability (D55): the collector the hosts send to, Prometheus, Loki, Tempo and Grafana.
        // The Aspire Dashboard was M2's stand-in; a cluster made then still has it, which this removes.
        Step("Platform: OpenTelemetry Collector, Prometheus, Loki, Tempo, Grafana");
        Kubectl("delete", "deployment,service,httproute", "aspire-dashboard", "-n", "platform", "--ignore-not-found");
        Helm("loki", Chart("loki", Pins.GrafanaRepo, Pins.LokiChart), repo: null, version: null, "platform", PathOf("platform/loki/values.yaml"));
        Helm("tempo", Chart("tempo", Pins.GrafanaCommunityRepo, Pins.TempoChart), repo: null, version: null, "platform", PathOf("platform/tempo/values.yaml"));
        Helm("opentelemetry-collector", Chart("opentelemetry-collector", Pins.OpenTelemetryRepo, Pins.CollectorChart), repo: null, version: null, "platform", PathOf("platform/opentelemetry-collector/values.yaml"));
        Helm("prometheus", Chart("prometheus", Pins.PrometheusRepo, Pins.PrometheusChart), repo: null, version: null, "platform", PathOf("platform/prometheus/values.yaml"));
        Helm("grafana", Chart("grafana", Pins.GrafanaCommunityRepo, Pins.GrafanaChart), repo: null, version: null, "platform", PathOf("platform/grafana/values.yaml"));
        Kubectl("apply", "-f", PathOf("platform/manifests/grafana-route.yaml"));
    }

    /// <summary>
    /// The lab's secrets (ADR-0025, D33), generated once per cluster and kept in a Secret that every
    /// lab namespace gets a copy of: Keycloak and the product must agree on the client secrets.
    /// </summary>
    private Dictionary<string, string> EnsureSecrets()
    {
        var existing = Capture(_tools.Kubectl, ["--context", Context, "get", "secret", "lab-secrets", "-n", "platform", "-o", "jsonpath={.data}", "--ignore-not-found"]);
        var values = string.IsNullOrWhiteSpace(existing)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(existing)!
                .ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetString(Convert.FromBase64String(pair.Value)));

        // Every secret the lab needs; one a later version added is made for an existing cluster too.
        foreach (var (key, generate) in new (string, Func<string>)[]
        {
            ("keycloak-admin-password", () => Secret(24)),
            ("fansite-client-secret", () => Secret(32)),
            ("organizers-client-secret", () => Secret(32)),
            ("checkin-client-secret", () => Secret(32)),
            ("localpay-webhook-secret", () => Secret(32)),
            ("ticketing-master-key", () => Secret(48)),
            ("grafana-admin-user", () => "admin"),
            ("grafana-admin-password", () => Secret(24)),
            ("backup-access-key", () => Secret(20)),
            ("backup-secret-key", () => Secret(40)),
        })
        {
            values.TryAdd(key, generate());
        }

        foreach (var space in new[] { "platform", "eventticketing", "lab" })
        {
            var data = string.Join('\n', values.Select(pair => $"  {pair.Key}: \"{pair.Value}\""));
            Apply($"""
                apiVersion: v1
                kind: Secret
                metadata:
                  name: lab-secrets
                  namespace: {space}
                type: Opaque
                stringData:
                {data}
                """);
        }

        return values;

        static string Secret(int length) => RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", length);
    }

    private void PrintAddresses()
    {
        Console.WriteLine("The lab, on the usual ports (ADR-0053):");
        foreach (var (port, what) in Addresses)
        {
            Console.WriteLine($"  {what,-18} http://localhost:{port}");
        }

        var adminPassword = Capture(_tools.Kubectl, ["--context", Context, "get", "secret", "lab-secrets", "-n", "platform", "-o", "jsonpath={.data.keycloak-admin-password}"]);
        Console.WriteLine($"  Keycloak admin     user admin, password {Encoding.UTF8.GetString(Convert.FromBase64String(adminPassword))} (generated for this cluster)");
        var grafanaPassword = Capture(_tools.Kubectl, ["--context", Context, "get", "secret", "lab-secrets", "-n", "platform", "-o", "jsonpath={.data.grafana-admin-password}"]);
        Console.WriteLine($"  Grafana admin      user admin, password {Encoding.UTF8.GetString(Convert.FromBase64String(grafanaPassword))} (no sign-in needed to look and explore)");
        Console.WriteLine("  Fans               ana, ben, cid, … (password = name); olga (organizer), adam (admin)");
    }

    private int ClusterNodes() => Capture(_tools.Kind, ["get", "nodes", "--name", ClusterName]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    private int ConfiguredNodes() => File.ReadAllLines(PathOf("platform/kind/cluster.yaml")).Count(line => line.TrimStart().StartsWith("- role:", StringComparison.Ordinal));

    private bool ClusterExists() =>
        Capture(_tools.Kind, ["get", "clusters"]).Split('\n', StringSplitOptions.TrimEntries).Contains(ClusterName);

    private static bool InUse(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>
    /// A pinned upstream chart, downloaded once into .tools/charts: fetching a repository's index on
    /// every `up` is slow (Traefik's is several megabytes), and the cache also lets `up` run offline.
    /// </summary>
    private string Chart(string name, string repo, string version)
    {
        var directory = Path.Combine(root, ".tools", "charts");
        var archive = Path.Combine(directory, $"{name}-{version}.tgz");
        if (!File.Exists(archive))
        {
            Directory.CreateDirectory(directory);
            Run(_tools.Helm, ["pull", name, "--repo", repo, "--version", version, "--destination", directory]);
        }

        return archive;
    }

    /// <summary>Installs or upgrades a release and waits until it is ready: a pinned chart from a repository, or a local one.</summary>
    private void Helm(string release, string chart, string? repo, string? version, string space, string? values, params string[] settings)
    {
        // Helm 4 applies server-side. The lab's operators (the Scaling Lab, `lab flag`) change fields
        // the charts set too; the charts render what runs (lookup), so a conflict can only be a
        // flag a release adds, and forcing takes the field back without changing a value that runs.
        List<string> arguments =
        [
            "--kube-context", Context, "upgrade", "--install", release, chart,
            "--namespace", space, "--create-namespace", "--wait", "--timeout", "10m", "--force-conflicts",
        ];
        if (repo is not null && version is not null)
        {
            arguments.AddRange(["--repo", repo, "--version", version]);
        }

        if (values is not null)
        {
            arguments.AddRange(["--values", values]);
        }

        foreach (var setting in settings)
        {
            arguments.AddRange(["--set", setting]);
        }

        Run(_tools.Helm, arguments);
    }

    private void Kubectl(params string[] arguments) => Run(_tools.Kubectl, ["--context", Context, .. arguments]);

    private void Apply(string manifest) => Run(_tools.Kubectl, ["--context", Context, "apply", "-f", "-"], stdin: manifest);

    /// <summary>A command in SeaweedFS's shell, which talks to its master inside the pod.</summary>
    private string Weed(string command) =>
        Capture(_tools.Kubectl, ["--context", Context, "exec", "-i", "-n", "platform", "deployment/seaweedfs", "--", "weed", "shell"], stdin: command + "\n");

    /// <summary>Runs SQL with psql in a database instance's pod, as postgres; returns its rows, a line each, fields split by '|'.</summary>
    private string[] Sql(string pod, string sql) =>
        Capture(_tools.Kubectl,
                ["--context", Context, "exec", "-i", "-n", "eventticketing", pod, "-c", "postgres", "--",
                 "psql", "-U", "postgres", "-d", "eventticketing", "-v", "ON_ERROR_STOP=1", "-q", "-A", "-t", "-F", "|"],
                stdin: sql)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string PathOf(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Step(string name) => Console.WriteLine($"\n== {name}  ({_clock.Elapsed:mm\\:ss})");

    /// <summary>Runs a command with its output shown; fails the lab if it fails.</summary>
    private static void Run(string file, IEnumerable<string> arguments, string? stdin = null)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardInput = stdin is not null };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new LabException($"Couldn't start {file}.");
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new LabException($"{Path.GetFileName(file)} {string.Join(' ', start.ArgumentList)} failed (exit code {process.ExitCode}).");
        }
    }

    /// <summary>Whether a command succeeds; what it prints is discarded.</summary>
    private static bool Succeeds(string file, IEnumerable<string> arguments, string? stdin = null)
    {
        try
        {
            Capture(file, arguments, stdin: stdin);
            return true;
        }
        catch (LabException)
        {
            return false;
        }
    }

    /// <summary>Runs a command and returns what it printed.</summary>
    private static string Capture(string file, IEnumerable<string> arguments, string? failure = null, Dictionary<string, string>? environment = null, string? stdin = null)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? [])
        {
            start.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new LabException($"Couldn't start {file}.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new LabException(failure ?? $"{file} isn't installed.");
        }

        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new LabException(failure ?? $"{Path.GetFileName(file)} {string.Join(' ', start.ArgumentList)} failed: {errors.Result.Trim()}");
            }

            return output.Trim();
        }
    }
}

/// <summary>
/// Pinned versions, each at least two weeks old when chosen, with the checksums their projects
/// publish. Changing a version means changing its checksums in the same commit.
/// </summary>
static class Pins
{
    public const string Kind = "v0.33.0";
    public const string Kubectl = "v1.37.0";
    public const string Helm = "v4.3.0";

    public static readonly Dictionary<string, string> KindSha256 = new()
    {
        ["windows-amd64"] = "4b22adaa135368c5a465d56bbd8e520cbea87272a06ca00b6078e7b81515c9fc",
        ["linux-amd64"] = "aee6151561422756b764a4ae28e7f44cda5af5a9eead3cc9985112b1de8d8e0d",
        ["darwin-arm64"] = "0c8c7dbe5e23594a198b786c4bc13dacc101fa6196b0cb0b23a1ca44e61f4b4f",
        ["darwin-amd64"] = "5a99f26f57246dc9319dd294803313197a0f34d33c525b3ea8b655db5916ece0",
    };

    public static readonly Dictionary<string, string> KubectlSha256 = new()
    {
        ["windows-amd64"] = "4721b614a67bb4932a0369e61f4a323d8c6ca00943d3a2ff14837c124da06f0e",
        ["linux-amd64"] = "6129359f4e1f3848a5572ccb0b26cf28b8ca08cef38c95a765b2f64a2c961a2f",
        ["darwin-arm64"] = "583beedaebe422e71d3f1a96acef8b1fef86ea2f09a45ad01aa6c9ce287c1380",
        ["darwin-amd64"] = "d5276c0f4fde77fc446070290f345944a7f1fda153df6b960e5fde93b7a9bccd",
    };

    public static readonly Dictionary<string, string> HelmSha256 = new()
    {
        ["windows-amd64"] = "304ea163cce4d9ad14e189c01846c6a34de9cfdfe48536ae54b2e8ba7884e67c",
        ["linux-amd64"] = "86584a54def73570558f66f5111cc53dfed56689637ae32c1201205d494f54fb",
        ["darwin-arm64"] = "d3870437e1e95b67f8edbde964156c84a26503f560821d40c542441658934fba",
        ["darwin-amd64"] = "347a784877e0e20eac865e8d1c36a80f6bb0861d6f29abd34defb6570ef95d92",
    };

    // The version the Traefik chart's README names.
    public const string GatewayApiCrds = "https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.6.1/standard-install.yaml";
    public const string GatewayApiCrdsSha256 = "24d931f22abd8e40c973264319ead7cfa09d0fb7716b7ab1ee2ff174cb063a73";

    public const string TraefikRepo = "https://traefik.github.io/charts";
    public const string TraefikChart = "41.6.1";
    public const string CloudNativePgRepo = "https://cloudnative-pg.github.io/charts";
    public const string CloudNativePgChart = "0.29.1";
    public const string BarmanCloudPluginChart = "0.8.1"; // from the CloudNativePG repository
    public const string CertManagerRepo = "https://charts.jetstack.io";
    public const string CertManagerChart = "v1.21.2";
    public const string MetricsServerRepo = "https://kubernetes-sigs.github.io/metrics-server/";
    public const string MetricsServerChart = "3.14.0";
    public const string KedaRepo = "https://kedacore.github.io/charts";
    public const string KedaChart = "2.21.0";
    public const string PrometheusRepo = "https://prometheus-community.github.io/helm-charts";
    public const string PrometheusChart = "29.35.0";
    // Loki's chart is still Grafana Labs'; Grafana's and Tempo's moved to the community repo in 2026.
    public const string GrafanaRepo = "https://grafana.github.io/helm-charts";
    public const string LokiChart = "7.3.0";
    public const string GrafanaCommunityRepo = "https://grafana-community.github.io/helm-charts";
    public const string GrafanaChart = "13.2.7";
    public const string TempoChart = "2.4.0";
    public const string OpenTelemetryRepo = "https://open-telemetry.github.io/opentelemetry-helm-charts";
    public const string CollectorChart = "0.175.1";
}

/// <summary>kind, kubectl and Helm in .tools/bin, downloaded at their pinned versions and verified.</summary>
sealed class Tools(string directory)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly string Platform =
        (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux") + "-" +
        (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64");

    private static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : "";

    public string Kind => Path.Combine(directory, "kind" + Exe);

    public string Kubectl => Path.Combine(directory, "kubectl" + Exe);

    public string Helm => Path.Combine(directory, "helm" + Exe);

    public bool Installed => File.Exists(Kind) && File.Exists(Kubectl) && File.Exists(Helm);

    public async Task EnsureAsync()
    {
        Directory.CreateDirectory(directory);
        var (os, arch) = (Platform.Split('-')[0], Platform.Split('-')[1]);

        await EnsureAsync(Kind, Pins.Kind, async () => await DownloadAsync(
            $"https://github.com/kubernetes-sigs/kind/releases/download/{Pins.Kind}/kind-{Platform}", Sha256(Pins.KindSha256)));

        await EnsureAsync(Kubectl, Pins.Kubectl, async () => await DownloadAsync(
            $"https://dl.k8s.io/release/{Pins.Kubectl}/bin/{os}/{arch}/kubectl{Exe}", Sha256(Pins.KubectlSha256)));

        await EnsureAsync(Helm, Pins.Helm, async () =>
        {
            var archive = await DownloadAsync(
                $"https://get.helm.sh/helm-{Pins.Helm}-{Platform}.{(OperatingSystem.IsWindows() ? "zip" : "tar.gz")}", Sha256(Pins.HelmSha256));
            return Extract(archive, $"{Platform}/helm{Exe}");
        });
    }

    /// <summary>Downloads a file once into .tools/ and checks its checksum every time it is used.</summary>
    public async Task<string> DownloadVerifiedAsync(string url, string sha256, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path) || Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))) != sha256)
        {
            await File.WriteAllBytesAsync(path, await DownloadAsync(url, sha256));
        }

        return path;
    }

    private async Task EnsureAsync(string path, string version, Func<Task<byte[]>> download)
    {
        var stamp = path + ".version";
        if (File.Exists(path) && File.Exists(stamp) && (await File.ReadAllTextAsync(stamp)).Trim() == version)
        {
            return;
        }

        Console.WriteLine($"  {Path.GetFileNameWithoutExtension(path)} {version}");
        await File.WriteAllBytesAsync(path, await download());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }

        await File.WriteAllTextAsync(stamp, version);
    }

    private static string Sha256(Dictionary<string, string> checksums) =>
        checksums.TryGetValue(Platform, out var sha256) ? sha256 : throw new LabException($"No pinned checksum for {Platform}: add one in lab.cs.");

    private static async Task<byte[]> DownloadAsync(string url, string sha256)
    {
        var bytes = await Http.GetByteArrayAsync(new Uri(url));
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return actual == sha256 ? bytes : throw new LabException($"{url} has checksum {actual}, not the pinned {sha256}. Not using it.");
    }

    private static byte[] Extract(byte[] archive, string entryName)
    {
        using var input = new MemoryStream(archive);
        if (OperatingSystem.IsWindows())
        {
            using var zip = new ZipArchive(input);
            using var entry = (zip.GetEntry(entryName) ?? throw new LabException($"{entryName} isn't in the archive.")).Open();
            using var output = new MemoryStream();
            entry.CopyTo(output);
            return output.ToArray();
        }

        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } tarEntry)
        {
            if (tarEntry.Name == entryName && tarEntry.DataStream is { } data)
            {
                using var output = new MemoryStream();
                data.CopyTo(output);
                return output.ToArray();
            }
        }

        throw new LabException($"{entryName} isn't in the archive.");
    }
}
