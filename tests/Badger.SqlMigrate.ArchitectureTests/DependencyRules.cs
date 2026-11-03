using System.Reflection;
using Badger.SqlMigrate.Cli;
using Badger.SqlMigrate.Connectivity;
using Badger.SqlMigrate.Core.Engine;

namespace Badger.SqlMigrate.ArchitectureTests;

/// <summary>
/// Guards the provider boundary: vendor drivers may only be referenced by provider assemblies, so adding
/// PostgreSQL/MySQL/SQLite never requires touching the engine or connectivity code.
/// </summary>
public class DependencyRules
{
    private static readonly string[] VendorAssemblies =
    [
        "Microsoft.Data.SqlClient",
        "Microsoft.SqlServer.TransactSql.ScriptDom",
        "Npgsql",
        "MySqlConnector",
        "MySql.Data",
        "Microsoft.Data.Sqlite",
    ];

    public static TheoryData<string> VendorNeutralAssemblies => new()
    {
        typeof(MigrationEngine).Assembly.GetName().Name!,
        typeof(SshTunnel).Assembly.GetName().Name!,
        typeof(Commands).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(VendorNeutralAssemblies))]
    public void Assembly_does_not_reference_vendor_drivers(string assemblyName)
    {
        var references = Assembly.Load(assemblyName).GetReferencedAssemblies().Select(a => a.Name).ToList();
        var offending = references.Where(r => VendorAssemblies.Contains(r, StringComparer.Ordinal)).ToList();
        Assert.True(offending.Count == 0, $"{assemblyName} references {string.Join(", ", offending)}; keep vendor code in a Providers.* assembly.");
    }

    [Fact]
    public void Core_has_no_package_dependencies_beyond_the_bcl()
    {
        var references = typeof(MigrationEngine).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        var nonBcl = references.Where(r => !r.StartsWith("System", StringComparison.Ordinal) && r != "netstandard" && r != "mscorlib").ToList();
        Assert.Empty(nonBcl);
    }
}
