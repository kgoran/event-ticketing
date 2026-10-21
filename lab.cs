#:property PublishAot=false

// The lab environment (ADR-0022, M2 plan): a local Kubernetes cluster built from this repository.
//
//   dotnet run lab.cs -- up       create the cluster if there is none, then build and deploy the current source
//   dotnet run lab.cs -- reset    keep the cluster and platform; a fresh database, redeployed, reseeded
//   dotnet run lab.cs -- status   what runs, and where to open it
//   dotnet run lab.cs -- check    does it work: hosts ready, addresses answering, sign-in, a burst of load
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

var lab = new Lab(Lab.FindRoot());
try
{
    return (args.FirstOrDefault() ?? "help") switch
    {
        "up" => await lab.UpAsync(),
        "down" => lab.Down(),
        "reset" => await lab.ResetAsync(),
        "status" => lab.Status(),
        "check" => await lab.CheckAsync(),
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
        (5100, "fan site"), (5101, "PublicApi"), (5102, "BackOfficeApi"), (5103, "WebhookIngress"),
        (5201, "LocalPay"), (5300, "Scaling Lab"), (5301, "load generator"),
        (8025, "Mailpit"), (8180, "Keycloak"), (18888, "Aspire Dashboard"),
    ];

    /// <summary>The images `up` builds, and the projects they come from.</summary>
    private static readonly (string Image, string Project)[] Images =
    [
        ("publicapi", "product/src/Hosts/PublicApi"),
        ("backofficeapi", "product/src/Hosts/BackOfficeApi"),
        ("webhookingress", "product/src/Hosts/WebhookIngress"),
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

        // Only what the node lacks: kind exports every image it is given before it looks.
        Step("Images: into the cluster");
        var present = Capture("docker", ["exec", $"{ClusterName}-control-plane", "crictl", "images", "-o", "json"]);
        var missing = Images.Select(pair => $"eventticketing/{pair.Image}:{tag}")
            .Where(image => !present.Contains($"\"docker.io/{image}\"", StringComparison.Ordinal))
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
        Helm("eventticketing-database", PathOf("product/deploy/database"), repo: null, version: null, "eventticketing", values: null);
        Kubectl("wait", "--for=condition=Ready", "cluster/eventticketing", "-n", "eventticketing", "--timeout=300s");

        Step("Product: migrations and hosts");
        Helm("eventticketing", PathOf("product/deploy/chart"), repo: null, version: null, "eventticketing",
            PathOf("product/deploy/chart/values-lab.yaml"), $"image.tag={tag}");
        Kubectl("get", "deployments", "-n", "eventticketing");
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
    public async Task<int> CheckAsync()
    {
        if (!_tools.Installed || !ClusterExists())
        {
            throw new LabException("No lab cluster. Start one with: dotnet run lab.cs -- up");
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
        foreach (var (port, what) in Addresses)
        {
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

        Console.WriteLine();
        Console.WriteLine(failures.Count == 0 ? "The lab works." : $"{failures.Count} check(s) failed.");
        return failures.Count == 0 ? 0 : 1;
    }

    public int Status()
    {
        if (!_tools.Installed || !ClusterExists())
        {
            Console.WriteLine("No lab cluster. Start one with: dotnet run lab.cs -- up");
            return 0;
        }

        Run(_tools.Kubectl, ["--context", Context, "get", "pods", "--all-namespaces", "-l", "!job-name", "-o", "wide"]);
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

        Step("Platform: Traefik, CloudNativePG, metrics-server");
        Helm("traefik", Chart("traefik", Pins.TraefikRepo, Pins.TraefikChart), repo: null, version: null, "platform", PathOf("platform/traefik/values.yaml"));
        Helm("cloudnative-pg", Chart("cloudnative-pg", Pins.CloudNativePgRepo, Pins.CloudNativePgChart), repo: null, version: null, "cnpg-system", values: null);
        Helm("metrics-server", Chart("metrics-server", Pins.MetricsServerRepo, Pins.MetricsServerChart), repo: null, version: null, "kube-system", PathOf("platform/metrics-server/values.yaml"));
        Kubectl("wait", "--for=condition=Programmed", "gateway/lab", "-n", "platform", "--timeout=120s");

        Step("Platform: secrets");
        EnsureSecrets();

        Step("Platform: Keycloak, Mailpit, Aspire Dashboard");
        var realm = PathOf("dev/AppHost/Realms/eventticketing-realm.json");
        Apply(Capture(_tools.Kubectl, ["--context", Context, "create", "configmap", "keycloak-realm", "-n", "platform", $"--from-file={realm}", "--dry-run=client", "-o", "yaml"]));
        // Keycloak imports the realm only when it starts: a changed realm file restarts it.
        var realmHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(realm)))[..16];
        Apply(File.ReadAllText(PathOf("platform/manifests/keycloak.yaml")).Replace("REALM_HASH", realmHash, StringComparison.Ordinal));
        Kubectl("apply", "-f", PathOf("platform/manifests/mailpit.yaml"));
        Kubectl("apply", "-f", PathOf("platform/manifests/aspire-dashboard.yaml"));

        foreach (var deployment in new[] { "keycloak", "mailpit", "aspire-dashboard" })
        {
            Kubectl("rollout", "status", $"deployment/{deployment}", "-n", "platform", "--timeout=300s");
        }
    }

    /// <summary>
    /// The lab's secrets (ADR-0025, D33), generated once per cluster and kept in a Secret that every
    /// lab namespace gets a copy of: Keycloak and the product must agree on the client secrets.
    /// </summary>
    private void EnsureSecrets()
    {
        var existing = Capture(_tools.Kubectl, ["--context", Context, "get", "secret", "lab-secrets", "-n", "platform", "-o", "jsonpath={.data}", "--ignore-not-found"]);
        var values = string.IsNullOrWhiteSpace(existing)
            ? new Dictionary<string, string>
            {
                ["keycloak-admin-password"] = Secret(24),
                ["fansite-client-secret"] = Secret(32),
                ["organizers-client-secret"] = Secret(32),
                ["localpay-webhook-secret"] = Secret(32),
                ["ticketing-master-key"] = Secret(48),
            }
            : JsonSerializer.Deserialize<Dictionary<string, string>>(existing)!
                .ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetString(Convert.FromBase64String(pair.Value)));

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
        Console.WriteLine("  Fans               ana, ben, cid, … (password = name); olga (organizer), adam (admin)");
    }

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
        List<string> arguments =
        [
            "--kube-context", Context, "upgrade", "--install", release, chart,
            "--namespace", space, "--create-namespace", "--wait", "--timeout", "10m",
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
    private static bool Succeeds(string file, IEnumerable<string> arguments)
    {
        try
        {
            Capture(file, arguments);
            return true;
        }
        catch (LabException)
        {
            return false;
        }
    }

    /// <summary>Runs a command and returns what it printed.</summary>
    private static string Capture(string file, IEnumerable<string> arguments, string? failure = null, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
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
    public const string MetricsServerRepo = "https://kubernetes-sigs.github.io/metrics-server/";
    public const string MetricsServerChart = "3.14.0";
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
