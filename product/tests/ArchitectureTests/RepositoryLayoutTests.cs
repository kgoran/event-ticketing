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
    public void Every_product_project_except_the_hosts_is_checked_by_these_tests()
    {
        var hosts = Path.Combine(ProductAssemblies.ProductSource.FullName, "Hosts") + Path.DirectorySeparatorChar;
        var loaded = ProductAssemblies.All.Select(assembly => assembly.GetName().Name).ToHashSet();

        var missing = Directory.GetFiles(ProductAssemblies.ProductSource.FullName, "*.csproj", SearchOption.AllDirectories)
            .Where(project => !project.StartsWith(hosts, StringComparison.OrdinalIgnoreCase))
            .Select(project => ProductAssemblies.Prefix + Path.GetFileNameWithoutExtension(project))
            .Where(assemblyName => !loaded.Contains(assemblyName))
            .ToList();

        missing.Should().BeEmpty("add a ProjectReference to ArchitectureTests.csproj for each of these");
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
