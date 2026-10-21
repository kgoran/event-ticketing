#:property PublishAot=false

// The lab environment (ADR-0022, M2 plan): a local Kubernetes cluster built from this repository.
//
//   dotnet run lab.cs -- up       create the cluster if there is none, install the platform
//   dotnet run lab.cs -- status   what runs, and where to open it
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
        "status" => lab.Status(),
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

              dotnet run lab.cs -- up       create the cluster if there is none, install the platform
              dotnet run lab.cs -- status   what runs, and where to open it
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

        Step("Ready");
        PrintAddresses();
        return 0;
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
        Helm("traefik", "traefik", Pins.TraefikRepo, Pins.TraefikChart, "platform", PathOf("platform/traefik/values.yaml"));
        Helm("cloudnative-pg", "cloudnative-pg", Pins.CloudNativePgRepo, Pins.CloudNativePgChart, "cnpg-system", values: null);
        Helm("metrics-server", "metrics-server", Pins.MetricsServerRepo, Pins.MetricsServerChart, "kube-system", PathOf("platform/metrics-server/values.yaml"));
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

    private void Helm(string release, string chart, string repo, string version, string space, string? values)
    {
        List<string> arguments =
        [
            "--kube-context", Context, "upgrade", "--install", release, chart, "--repo", repo, "--version", version,
            "--namespace", space, "--create-namespace", "--wait", "--timeout", "5m",
        ];
        if (values is not null)
        {
            arguments.AddRange(["--values", values]);
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

    /// <summary>Runs a command and returns what it printed.</summary>
    private static string Capture(string file, IEnumerable<string> arguments, string? failure = null)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
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
