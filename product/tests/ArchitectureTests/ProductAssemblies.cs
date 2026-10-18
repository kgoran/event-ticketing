using System.Reflection;

namespace EventTicketing.ArchitectureTests;

/// <summary>The repository layout and the product assemblies the tests inspect.</summary>
internal static class ProductAssemblies
{
    public const string Prefix = "EventTicketing.";

    /// <summary>ADR-0006: which other modules' contracts each module may use.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedModuleDependencies = new Dictionary<string, string[]>
    {
        ["Catalog"] = [],
        ["Organizers"] = [],
        ["Payments"] = [],
        ["Inventory"] = ["Catalog"],
        ["WaitingRoom"] = ["Catalog", "Inventory"],
        ["Orders"] = ["Catalog", "Inventory", "Payments"],
        ["Ticketing"] = ["Catalog", "Orders"],
        ["CheckIn"] = ["Catalog", "Ticketing"],
        ["Notifications"] = ["Orders", "Ticketing"],
        ["Reporting"] = ["Catalog", "Inventory", "Orders", "Payments", "Ticketing", "CheckIn", "Organizers"],
    };

    public static DirectoryInfo RepositoryRoot { get; } = FindRepositoryRoot();

    public static DirectoryInfo ProductSource { get; } = new(Path.Combine(RepositoryRoot.FullName, "product", "src"));

    /// <summary>Every EventTicketing assembly in the test output, loaded once.</summary>
    public static IReadOnlyList<Assembly> All { get; } = LoadAll();

    /// <summary>Module assemblies, e.g. EventTicketing.Orders.Domain, with their module and layer ("" for a two-project module's main project).</summary>
    public static IReadOnlyList<(Assembly Assembly, string Module, string Layer)> Modules { get; } =
        All.Select(Describe).OfType<(Assembly, string, string)>().ToList();

    public static IEnumerable<AssemblyName> ProductReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Where(reference => reference.Name?.StartsWith(Prefix, StringComparison.Ordinal) == true);

    /// <summary>Splits "EventTicketing.Orders.Domain" into ("Orders", "Domain"); null if it isn't a module assembly.</summary>
    public static (string Module, string Layer)? ModuleOf(string? assemblyName)
    {
        if (assemblyName is null || !assemblyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = assemblyName[Prefix.Length..].Split('.', 2);
        return AllowedModuleDependencies.ContainsKey(parts[0])
            ? (parts[0], parts.Length > 1 ? parts[1] : string.Empty)
            : null;
    }

    private static (Assembly, string, string)? Describe(Assembly assembly) =>
        ModuleOf(assembly.GetName().Name) is { } module ? (assembly, module.Module, module.Layer) : null;

    private static List<Assembly> LoadAll() =>
        Directory.GetFiles(AppContext.BaseDirectory, Prefix + "*.dll")
            .Where(path => !Path.GetFileName(path).StartsWith(Prefix + "ArchitectureTests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .ToList();

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException("Repository root (the folder with global.json) not found.");
    }
}
