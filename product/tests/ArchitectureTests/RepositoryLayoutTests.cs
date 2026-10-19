using System.Xml.Linq;

namespace EventTicketing.ArchitectureTests;

/// <summary>ADR-0021: the product stands alone; tooling and the dev composition depend on it, never the reverse.</summary>
public sealed class RepositoryLayoutTests
{
    [Fact]
    public void Product_projects_reference_neither_tooling_nor_dev()
    {
        var forbiddenRoots = new[] { "tooling", "dev" }
            .Select(folder => Path.Combine(ProductAssemblies.RepositoryRoot.FullName, folder) + Path.DirectorySeparatorChar)
            .ToArray();

        var violations =
            from project in ProductProjects()
            from reference in ProjectReferences(project)
            where forbiddenRoots.Any(root => reference.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            select $"{Relative(project)} → {Relative(reference)}";

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Every_product_project_except_the_hosts_and_frontends_is_checked_by_these_tests()
    {
        var compositionRoots = new[] { "Hosts", "Frontends" }
            .Select(folder => Path.Combine(ProductAssemblies.ProductSource.FullName, folder) + Path.DirectorySeparatorChar)
            .ToArray();
        var loaded = ProductAssemblies.All.Select(assembly => assembly.GetName().Name).ToHashSet();

        var missing = Directory.GetFiles(ProductAssemblies.ProductSource.FullName, "*.csproj", SearchOption.AllDirectories)
            .Where(project => !compositionRoots.Any(root => project.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            .Select(project => ProductAssemblies.Prefix + Path.GetFileNameWithoutExtension(project))
            .Where(assemblyName => !loaded.Contains(assemblyName))
            .ToList();

        missing.Should().BeEmpty("add a ProjectReference to ArchitectureTests.csproj for each of these");
    }

    /// <summary>
    /// ADR-0049: a frontend uses the product only through its HTTP APIs, like any other client, so it
    /// may reference ServiceDefaults (telemetry, health checks) but no module and no Shared project.
    /// </summary>
    [Fact]
    public void Frontends_use_the_product_only_through_its_apis()
    {
        var frontends = new DirectoryInfo(Path.Combine(ProductAssemblies.ProductSource.FullName, "Frontends"));
        var allowed = Path.Combine(ProductAssemblies.ProductSource.FullName, "Hosts", "ServiceDefaults", "ServiceDefaults.csproj");

        var violations =
            from project in frontends.Exists ? Directory.GetFiles(frontends.FullName, "*.csproj", SearchOption.AllDirectories) : []
            from reference in ProjectReferences(project)
            where !string.Equals(reference, allowed, StringComparison.OrdinalIgnoreCase)
            select $"{Relative(project)} → {Relative(reference)}";

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Every_module_in_the_repository_is_one_of_the_agreed_modules()
    {
        var modulesFolder = new DirectoryInfo(Path.Combine(ProductAssemblies.ProductSource.FullName, "Modules"));
        var onDisk = modulesFolder.Exists ? modulesFolder.GetDirectories().Select(d => d.Name) : [];

        onDisk.Should().BeSubsetOf(ProductAssemblies.AllowedModuleDependencies.Keys, "ADR-0006 lists the modules");
    }

    private static IEnumerable<string> ProductProjects() =>
        Directory.GetFiles(Path.Combine(ProductAssemblies.RepositoryRoot.FullName, "product"), "*.csproj", SearchOption.AllDirectories);

    private static IEnumerable<string> ProjectReferences(string project)
    {
        var directory = Path.GetDirectoryName(project)!;
        return XDocument.Load(project)
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Select(include => Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar))));
    }

    private static string Relative(string path) => Path.GetRelativePath(ProductAssemblies.RepositoryRoot.FullName, path);
}
