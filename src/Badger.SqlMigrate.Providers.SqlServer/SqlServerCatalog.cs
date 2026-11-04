using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>Catalog reads 'bsm baseline' uses to see what a database already has.</summary>
public static class SqlServerCatalog
{
    /// <summary>Every user table, as schema.name.</summary>
    public static async Task<IReadOnlyList<string>> ReadTableNamesAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync("SELECT SCHEMA_NAME(schema_id) + N'.' + name AS name FROM sys.tables", null, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => (string)r["name"]!).ToList();
    }

    /// <summary>The definitions of every view, procedure, function and trigger (sys.sql_modules, as OBJECT_DEFINITION returns them).</summary>
    public static async Task<IReadOnlyList<string>> ReadModuleDefinitionsAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync("SELECT definition FROM sys.sql_modules WHERE definition IS NOT NULL", null, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => (string)r["definition"]!).ToList();
    }
}
