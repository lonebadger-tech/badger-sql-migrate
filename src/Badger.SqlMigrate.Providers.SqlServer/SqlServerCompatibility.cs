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

    private static async Task<bool> HasRetentionAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        if (session is SqlServerSession { HasHistoryRetention: { } known })
        {
            return known;
        }

        var rows = await session.QueryAsync("SELECT CASE WHEN COL_LENGTH('sys.tables', 'history_retention_period') IS NULL THEN 0 ELSE 1 END AS present;", null, cancellationToken).ConfigureAwait(false);
        var present = Convert.ToInt32(rows[0]["present"], System.Globalization.CultureInfo.InvariantCulture) == 1;
        if (session is SqlServerSession s)
        {
            s.HasHistoryRetention = present;
        }

        return present;
    }
}
