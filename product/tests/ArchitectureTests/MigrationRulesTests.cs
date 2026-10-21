using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EventTicketing.ArchitectureTests;

/// <summary>
/// DP3, expand/contract: a release's migrations run before it rolls out, while the previous release
/// still serves (D32), so a migration may not take away anything that release uses. A rename becomes
/// a new column kept in step with the old one (expand), and the old one is dropped in a later
/// release, by a migration whose name ends in "Contract". docs/database-changes.md
/// </summary>
public sealed partial class MigrationRulesTests
{
    /// <summary>The rule starts with M2's rolling deploys; earlier migrations only ever ran on new development databases.</summary>
    private const string RuleSince = "20261021000000";

    [Fact]
    public void Migrations_never_rename_and_drop_only_in_a_contract_step()
    {
        var violations =
            from assembly in ProductAssemblies.All
            from type in assembly.GetTypes()
            where typeof(Migration).IsAssignableFrom(type) && !type.IsAbstract
            let id = type.GetCustomAttribute<MigrationAttribute>()?.Id
            where id is not null && string.CompareOrdinal(id, RuleSince) >= 0
            from operation in ((Migration)Activator.CreateInstance(type)!).UpOperations
            let problem = Problem(operation, contract: type.Name.EndsWith("Contract", StringComparison.Ordinal))
            where problem is not null
            select $"{type.Name}: {problem}";

        violations.Should().BeEmpty("the release before a migration keeps serving while it rolls out (docs/database-changes.md)");
    }

    private static string? Problem(MigrationOperation operation, bool contract) => operation switch
    {
        RenameColumnOperation rename => $"renames {rename.Table}.{rename.Name}; add {rename.NewName} and keep both in step instead",
        RenameTableOperation rename => $"renames {rename.Name}; add {rename.NewName} and keep both in step instead",
        SqlOperation sql when Rename().IsMatch(sql.Sql) => "renames in SQL; add the new one and keep both in step instead",
        DropColumnOperation drop when !contract => $"drops {drop.Table}.{drop.Name} outside a contract step",
        DropTableOperation drop when !contract => $"drops {drop.Name} outside a contract step",
        SqlOperation sql when !contract && Drop().IsMatch(sql.Sql) => "drops a column or table in SQL outside a contract step",
        _ => null,
    };

    [GeneratedRegex(@"\bRENAME\s+(COLUMN|TO)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Rename();

    [GeneratedRegex(@"\bDROP\s+(COLUMN|TABLE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Drop();
}
