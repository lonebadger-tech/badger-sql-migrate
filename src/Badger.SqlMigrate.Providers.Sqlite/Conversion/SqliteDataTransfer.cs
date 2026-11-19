using System.Globalization;
using System.Runtime.CompilerServices;
using Badger.SqlMigrate.Core.Conversion;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Badger.SqlMigrate.Providers.Sqlite.Conversion;

/// <summary>
/// Reads tables row by row and loads them with one prepared INSERT, all in one transaction with foreign keys deferred.
/// Values from other vendors are stored the way SQLite applications usually keep them: booleans as 0/1, dates and
/// times as ISO 8601 text (UTC for time-zone-aware values), GUIDs as text, decimals as text that the column's affinity
/// converts.
/// </summary>
public sealed class SqliteDataTransfer : IDataTransfer
{
    private static SqliteSession Session(IDatabaseSession session) => session as SqliteSession ?? throw new ArgumentException("Not a SQLite session.", nameof(session));

    private static string Name(PortableName table) => SqliteLexer.Quote(table.Name);

    public async Task<IReadOnlyList<TransferColumn>> ColumnsAsync(IDatabaseSession session, PortableName table, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync("SELECT name, type FROM pragma_table_xinfo(@t) WHERE hidden = 0 ORDER BY cid;", new Dictionary<string, object?> { ["t"] = table.Name }, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new TransferColumn((string)r["name"]!, (string?)r["type"] ?? string.Empty)).ToList();
    }

    public async Task<long> CountAsync(IDatabaseSession session, PortableName table, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT count(*) AS n FROM {Name(table)};", null, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture);
    }

    public async IAsyncEnumerable<object?[]> ReadAsync(IDatabaseSession session, PortableName table, IReadOnlyList<TransferColumn> columns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var s = Session(session);
        await using var command = s.Connection.CreateCommand();
        command.Transaction = s.Transaction;
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(c => SqliteLexer.Quote(c.Name)))} FROM {Name(table)};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            yield return row;
        }
    }

    public async Task<long> WriteAsync(IDatabaseSession session, PortableName table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken cancellationToken)
    {
        var s = Session(session);
        await using var command = s.Connection.CreateCommand();
        command.Transaction = s.Transaction;
        command.CommandText = $"INSERT INTO {Name(table)} ({string.Join(", ", columns.Select(c => SqliteLexer.Quote(c.Name)))}) VALUES ({string.Join(", ", columns.Select((_, i) => "$p" + i))});";
        var parameters = columns.Select((_, i) => command.Parameters.Add("$p" + i, SqliteType.Text)).ToList();
        command.Prepare();
        long written = 0;
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            for (var i = 0; i < row.Length; i++)
            {
                var (value, type) = Value(row[i]);
                parameters[i].SqliteType = type;
                parameters[i].Value = value;
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            written++;
        }

        return written;
    }

    private static (object Value, SqliteType Type) Value(object? value) => value switch
    {
        null => (DBNull.Value, SqliteType.Text),
        bool b => (b ? 1L : 0L, SqliteType.Integer),
        byte or sbyte or short or ushort or int or uint or long => (Convert.ToInt64(value, CultureInfo.InvariantCulture), SqliteType.Integer),
        float or double => (Convert.ToDouble(value, CultureInfo.InvariantCulture), SqliteType.Real),
        decimal d => (d.ToString(CultureInfo.InvariantCulture), SqliteType.Text),
        byte[] bytes => (bytes, SqliteType.Blob),
        Guid g => (g.ToString(), SqliteType.Text),
        DateTimeOffset o => (o.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture), SqliteType.Text),
        DateTime d => ((d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d).ToString(d.TimeOfDay == TimeSpan.Zero && d.Kind == DateTimeKind.Unspecified ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture), SqliteType.Text),
        DateOnly d => (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), SqliteType.Text),
        TimeOnly t => (t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture), SqliteType.Text),
        TimeSpan t => (t.ToString(@"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'), SqliteType.Text),
        IFormattable f => (f.ToString(null, CultureInfo.InvariantCulture), SqliteType.Text),
        _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, SqliteType.Text),
    };

    public async Task TruncateAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync("PRAGMA defer_foreign_keys = ON;", null, cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            await session.ExecuteAsync($"DELETE FROM {Name(table)};", null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Triggers dropped for the load (SQLite cannot disable them), re-created before the commit.</summary>
    private readonly List<string> suspendedTriggers = [];

    public async Task BeginLoadAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await session.ExecuteAsync("PRAGMA defer_foreign_keys = ON;", null, cancellationToken).ConfigureAwait(false);
        suspendedTriggers.Clear();
        var names = tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await session.QueryAsync("SELECT name, tbl_name, sql FROM main.sqlite_master WHERE type = 'trigger' AND sql IS NOT NULL;", null, cancellationToken).ConfigureAwait(false))
        {
            if (names.Contains((string)r["tbl_name"]!))
            {
                suspendedTriggers.Add((string)r["sql"]!);
                await session.ExecuteAsync($"DROP TRIGGER {SqliteLexer.Quote((string)r["name"]!)};", null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Re-creates the triggers and commits (deferred foreign keys are checked then). AUTOINCREMENT counters follow explicit rowids by themselves.</summary>
    public async Task EndLoadAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        foreach (var sql in suspendedTriggers)
        {
            await session.ExecuteAsync(sql, null, cancellationToken).ConfigureAwait(false);
        }

        suspendedTriggers.Clear();
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
