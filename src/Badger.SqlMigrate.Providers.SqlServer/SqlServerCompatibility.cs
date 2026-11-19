using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>
/// Catalog columns that older SQL Server versions lack. A query that names a missing column fails to compile even when
/// the rows that would use it do not exist, so queries are adapted to the server before they run.
/// </summary>
internal static class SqlServerCompatibility
{
    /// <summary>
    /// sys.tables.history_retention_period and history_retention_period_unit_desc arrived in SQL Server 2017. SQL Server
    /// 2016 keeps temporal history forever, which 2017 and later report as -1 / INFINITE: substituting those values keeps
    /// the schema fingerprint of a temporal table the same on every version.
    /// </summary>
    public static async Task<string> AdaptAsync(IDatabaseSession session, string sql, CancellationToken cancellationToken)
    {
        if (!sql.Contains("history_retention_period", StringComparison.Ordinal) || await HasRetentionAsync(session, cancellationToken).ConfigureAwait(false))
        {
            return sql;
        }

        return sql
            .Replace("t.history_retention_period_unit_desc", "CAST(N'INFINITE' AS nvarchar(60))", StringComparison.Ordinal)
            .Replace("t.history_retention_period", "CAST(-1 AS int)", StringComparison.Ordinal);
    }

    /// <summary>
    /// sys.column_master_keys.allow_enclave_computations (Always Encrypted with secure enclaves) arrived in SQL Server 2019.
    /// Older servers cannot create enclave-enabled keys, so every column master key there is reported as not enclave-enabled.
    /// </summary>
    public static async Task<bool> HasEnclaveComputationsAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        if (session is SqlServerSession { HasEnclaveComputations: { } known })
        {
            return known;
        }

        var present = await HasColumnAsync(session, "sys.column_master_keys", "allow_enclave_computations", cancellationToken).ConfigureAwait(false);
        if (session is SqlServerSession s)
        {
            s.HasEnclaveComputations = present;
        }

        return present;
    }

    /// <summary>sys.columns.graph_type (graph node and edge tables) arrived in SQL Server 2017.</summary>
    public static async Task<bool> HasGraphTablesAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        if (session is SqlServerSession { HasGraphTables: { } known })
        {
            return known;
        }

        var present = await HasColumnAsync(session, "sys.columns", "graph_type", cancellationToken).ConfigureAwait(false);
        if (session is SqlServerSession s)
        {
            s.HasGraphTables = present;
        }

        return present;
    }

    private static async Task<bool> HasRetentionAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        if (session is SqlServerSession { HasHistoryRetention: { } known })
        {
            return known;
        }

        var present = await HasColumnAsync(session, "sys.tables", "history_retention_period", cancellationToken).ConfigureAwait(false);
        if (session is SqlServerSession s)
        {
            s.HasHistoryRetention = present;
        }

        return present;
    }

    private static async Task<bool> HasColumnAsync(IDatabaseSession session, string view, string column, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            "SELECT CASE WHEN COL_LENGTH(@view, @column) IS NULL THEN 0 ELSE 1 END AS present;",
            new Dictionary<string, object?> { ["view"] = view, ["column"] = column },
            cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(rows[0]["present"], System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}
