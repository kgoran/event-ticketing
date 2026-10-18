using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Contracts;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Modules;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace EventTicketing.ArchitectureTests;

/// <summary>The Shared projects (ADR-0026): dependencies point inward, and the inner ones stay framework-free.</summary>
public sealed class SharedKernelTests
{
    private static readonly System.Reflection.Assembly DomainAssembly = typeof(Error).Assembly;
    private static readonly System.Reflection.Assembly ApplicationAssembly = typeof(ICommand<>).Assembly;
    private static readonly System.Reflection.Assembly InfrastructureAssembly = typeof(IModule).Assembly;
    private static readonly System.Reflection.Assembly ContractsAssembly = typeof(IIntegrationEvent).Assembly;

    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(DomainAssembly, ApplicationAssembly, InfrastructureAssembly, ContractsAssembly)
        .Build();

    private static readonly IObjectProvider<IType> SharedDomain =
        Types().That().ResideInAssembly(DomainAssembly).As("Shared.Domain");

    private static readonly IObjectProvider<IType> SharedApplication =
        Types().That().ResideInAssembly(ApplicationAssembly).As("Shared.Application");

    private static readonly IObjectProvider<IType> SharedInfrastructure =
        Types().That().ResideInAssembly(InfrastructureAssembly).As("Shared.Infrastructure");

    private static readonly IObjectProvider<IType> SharedContracts =
        Types().That().ResideInAssembly(ContractsAssembly).As("Shared.Contracts");

    [Fact]
    public void Shared_domain_depends_on_no_other_product_code() =>
        Types().That().Are(SharedDomain)
            .Should().NotDependOnAny(Types().That().Are(SharedApplication).Or().Are(SharedInfrastructure).Or().Are(SharedContracts))
            .Check(Architecture);

    [Fact]
    public void Shared_application_does_not_depend_on_infrastructure() =>
        Types().That().Are(SharedApplication)
            .Should().NotDependOnAny(SharedInfrastructure)
            .Check(Architecture);

    [Fact]
    public void Shared_contracts_depend_on_no_other_product_code() =>
        Types().That().Are(SharedContracts)
            .Should().NotDependOnAny(Types().That().Are(SharedDomain).Or().Are(SharedApplication).Or().Are(SharedInfrastructure))
            .Check(Architecture);

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Npgsql")]
    public void Shared_domain_and_application_reference_no_infrastructure_framework(string framework)
    {
        var violations =
            from assembly in new[] { DomainAssembly, ApplicationAssembly }
            from reference in assembly.GetReferencedAssemblies()
            where reference.Name?.StartsWith(framework, StringComparison.Ordinal) == true
            select $"{assembly.GetName().Name} → {reference.Name}";

        violations.Should().BeEmpty();
    }
}
