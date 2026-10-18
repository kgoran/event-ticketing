namespace EventTicketing.ArchitectureTests;

/// <summary>
/// ADR-0002, ADR-0006 and ADR-0026, checked on the compiled assemblies. These tests pass trivially
/// until the first module exists, then guard every module added after it.
/// </summary>
public sealed class ModuleRulesTests
{
    [Fact]
    public void Modules_use_other_modules_only_through_the_contracts_ADR_0006_allows()
    {
        var violations =
            from module in ProductAssemblies.Modules
            from reference in ProductAssemblies.ProductReferences(module.Assembly)
            let target = ProductAssemblies.ModuleOf(reference.Name)
            where target is not null && target.Value.Module != module.Module
            where target.Value.Layer != "Contracts"
                || !ProductAssemblies.AllowedModuleDependencies[module.Module].Contains(target.Value.Module)
            select $"{module.Assembly.GetName().Name} → {reference.Name}";

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Layers_inside_a_module_only_point_inward()
    {
        // Forbidden references within one module (ADR-0026). Infrastructure may use the Domain through Application.
        var forbidden = new Dictionary<string, string[]>
        {
            ["Domain"] = ["Application", "Infrastructure", "Contracts", ""],
            ["Application"] = ["Infrastructure", ""],
            ["Contracts"] = ["Domain", "Application", "Infrastructure", ""],
        };

        var violations =
            from module in ProductAssemblies.Modules
            where forbidden.ContainsKey(module.Layer)
            from reference in ProductAssemblies.ProductReferences(module.Assembly)
            let target = ProductAssemblies.ModuleOf(reference.Name)
            where target is not null && target.Value.Module == module.Module
            where forbidden[module.Layer].Contains(target.Value.Layer)
            select $"{module.Assembly.GetName().Name} → {reference.Name}";

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Shared_projects_never_reference_a_module()
    {
        var violations =
            from assembly in ProductAssemblies.All
            where assembly.GetName().Name!.StartsWith(ProductAssemblies.Prefix + "Shared.", StringComparison.Ordinal)
            from reference in ProductAssemblies.ProductReferences(assembly)
            where ProductAssemblies.ModuleOf(reference.Name) is not null
            select $"{assembly.GetName().Name} → {reference.Name}";

        violations.Should().BeEmpty();
    }
}
