using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Badger.Trek.Core;
using Badger.Trek.Core.Conversion;
using Badger.Trek.Core.Providers;
using Badger.Trek.Providers.MySql.Scripting;
using MySqlConnector;

namespace Badger.Trek.Providers.MySql.Conversion;

/// <summary>
/// Reads tables with a streaming reader and loads them with multi-row INSERT statements (not LOAD DATA LOCAL INFILE,
/// which needs AllowLoadLocalInfile on the connection). The load runs in one transaction with foreign key checks
/// off; foreign keys are checked by query at the end, because MySQL does not re-check rows when the checks are
/// turned back on. MySQL cannot disable triggers, so triggers on the target tables fire for every loaded row.
/// TIMESTAMP columns are read and written in UTC (the session time zone is set to +00:00 while they are).
/// </summary>
public sealed class MySqlDataTransfer : IDataTransfer
{
    /// <summary>Rows per INSERT statement.</summary>
    private const int RowsPerStatement = 500;

    /// <summary>Parameters per INSERT statement, for wide tables.</summary>
    private const int ParametersPerStatement = 20_000;

    /// <summary>Approximate statement size before it is sent, well under the 64 MB default max_allowed_packet.</summary>
    private const long BytesPerStatement = 4 * 1024 * 1024;

    private static readonly HashSet<string> Spatial = new(StringComparer.OrdinalIgnoreCase)
    {
        "geometry", "point", "linestring", "polygon", "multipoint", "multilinestring", "multipolygon", "geometrycollection", "geomcollection",
    };

    /// <summary>Whether BeginLoadAsync opened the transaction (and EndLoadAsync commits it).</summary>
    private bool ownTransaction;

    private static MySqlSession Session(IDatabaseSession session) => session as MySqlSession ?? throw new ArgumentException("Not a MySQL session.", nameof(session));

    /// <summary>The session is in the database already; PortableName.Schema is the database name.</summary>
    private static string Name(PortableName table) => MySqlModel.Q(table.Name);

    public async Task<IReadOnlyList<TransferColumn>> ColumnsAsync(IDatabaseSession session, PortableName table, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync("""
            SELECT column_name AS name, column_type AS type
              FROM information_schema.columns
             WHERE table_schema = DATABASE() AND table_name = @t AND (generation_expression IS NULL OR generation_expression = '')
             ORDER BY ordinal_position;
            """, new Dictionary<string, object?> { ["t"] = table.Name }, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new TransferColumn(Text(r["name"]), Text(r["type"]))).ToList();
    }

    public async Task<long> CountAsync(IDatabaseSession session, PortableName table, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT COUNT(*) AS n FROM {Name(table)};", null, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture);
    }

    public async IAsyncEnumerable<object?[]> ReadAsync(IDatabaseSession session, PortableName table, IReadOnlyList<TransferColumn> columns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var s = Session(session);
        var select = string.Join(", ", columns.Select((c, i) => Expression(c) + $" AS c{i}"));
        var sql = $"SELECT {select} FROM {Name(table)};";
        var zone = await UtcAsync(session, columns, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new MySqlCommand(sql, s.Connection, s.Transaction) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new object?[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    row[i] = Cell(reader, i, columns[i].Type);
                }

                yield return row;
            }
        }
        finally
        {
            if (zone is not null)
            {
                await session.ExecuteAsync("SET time_zone = @z;", new Dictionary<string, object?> { ["z"] = zone }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The column as it is selected: spatial values as WKT text; CHAR(36) as text, because MySqlConnector reads that
    /// type as a GUID and fails on any other text.
    /// </summary>
    private static string Expression(TransferColumn column)
    {
        var name = MySqlModel.Q(column.Name);
        return BaseType(column.Type) switch
        {
            var t when Spatial.Contains(t) => $"ST_AsText({name})",
            "char" when column.Type.StartsWith("char(36)", StringComparison.OrdinalIgnoreCase) => $"CAST({name} AS CHAR)",
            _ => name,
        };
    }

    /// <summary>One value, as a vendor-neutral CLR value.</summary>
    private static object? Cell(MySqlDataReader reader, int i, string type)
    {
        if (reader.IsDBNull(i))
        {
            return null;
        }

        try
        {
            return Neutral(reader.GetValue(i), type);
        }
        catch (InvalidCastException) when (BaseType(type) is "date" or "datetime" or "timestamp")
        {
            // A zero date ('0000-00-00'), which no other database (nor System.DateTime) can hold.
            return null;
        }
        catch (OverflowException) when (BaseType(type) == "decimal")
        {
            // More digits than System.Decimal holds: keep the exact text.
            return reader.GetMySqlDecimal(i).ToString();
        }
    }

    private static object? Neutral(object value, string type) => value switch
    {
        MySqlDateTime m => m.IsValidDateTime ? Timestamp(m.GetDateTime(), type) : null,
        DateTime d => Timestamp(d, type),
        MySqlDecimal m => m.ToString(),
        ulong u when type == "bit(1)" => u != 0,
        _ => value,
    };

    /// <summary>TIMESTAMP values are read in UTC; DATETIME has no time zone.</summary>
    private static DateTime Timestamp(DateTime value, string type) =>
        BaseType(type) == "timestamp" ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value;

    public async Task<long> WriteAsync(IDatabaseSession session, PortableName table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken cancellationToken)
    {
        Session(session);
        var head = $"INSERT INTO {Name(table)} ({string.Join(", ", columns.Select(c => MySqlModel.Q(c.Name)))}) VALUES ";
        var perStatement = Math.Clamp(ParametersPerStatement / Math.Max(1, columns.Count), 1, RowsPerStatement);
        var sql = new StringBuilder(head);
        var parameters = new Dictionary<string, object?>();
        var (batch, bytes, written) = (0, 0L, 0L);

        async Task FlushAsync()
        {
            // The session runs it inside its open transaction.
            await session.ExecuteAsync(sql.Append(';').ToString(), parameters, cancellationToken).ConfigureAwait(false);
            written += batch;
            (batch, bytes) = (0, 0);
            sql.Clear().Append(head);
            parameters = [];
        }

        var zone = await UtcAsync(session, columns, cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                sql.Append(batch == 0 ? "(" : ", (");
                for (var i = 0; i < columns.Count; i++)
                {
                    var name = "p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                    var value = Value(i < row.Length ? row[i] : null, columns[i].Type);
                    sql.Append(i == 0 ? "@" : ", @").Append(name);
                    parameters[name] = value;
                    bytes += value switch
                    {
                        string text => text.Length * 3L,
                        byte[] data => data.Length * 2L,
                        _ => 24,
                    };
                }

                sql.Append(')');
                batch++;
                if (batch >= perStatement || bytes >= BytesPerStatement)
                {
                    await FlushAsync().ConfigureAwait(false);
                }
            }

            if (batch > 0)
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (zone is not null)
            {
                await session.ExecuteAsync("SET time_zone = @z;", new Dictionary<string, object?> { ["z"] = zone }, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return written;
    }

    /// <summary>
    /// A value from any vendor as a MySqlConnector parameter: GUIDs as their 36-character text (16 bytes in RFC 4122
    /// order for BINARY(16), as UUID_TO_BIN stores them), time-zone-aware and local times in UTC. Booleans become 1/0 in
    /// the session.
    /// </summary>
    private static object? Value(object? value, string type) => value switch
    {
        Guid g when type == "binary(16)" => g.ToByteArray(bigEndian: true),
        Guid g => g.ToString("D"),
        DateTimeOffset o => o.UtcDateTime,
        DateTime { Kind: DateTimeKind.Local } d => d.ToUniversalTime(),
        _ => value,
    };

    /// <summary>
    /// TRUNCATE TABLE (which also resets AUTO_INCREMENT) with foreign key checks off for the session, so tables that
    /// others reference can be emptied in any order; the setting is restored afterwards. It commits implicitly.
    /// </summary>
    public async Task TruncateAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        if (tables.Count == 0)
        {
            return;
        }

        var previous = Convert.ToInt64((await session.QueryAsync("SELECT @@session.foreign_key_checks AS v;", null, cancellationToken).ConfigureAwait(false))[0]["v"], CultureInfo.InvariantCulture);
        await session.ExecuteAsync("SET foreign_key_checks = 0;", null, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var table in tables)
            {
                await session.ExecuteAsync($"TRUNCATE TABLE {Name(table)};", null, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ExecuteAsync($"SET foreign_key_checks = {previous.ToString(CultureInfo.InvariantCulture)};", null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens the load's transaction and turns foreign key checks off for the session, so tables load in any
    /// order. Unique checks stay on: with them off InnoDB may miss duplicates (text the source compared case-sensitively,
    /// say). MySQL has no way to disable triggers per table: triggers on the target tables fire during the load.
    /// </summary>
    public async Task BeginLoadAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        Session(session);
        ownTransaction = !session.InTransaction;
        if (ownTransaction)
        {
            await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }

        await session.ExecuteAsync("SET foreign_key_checks = 0;", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the checks back on and checks every foreign key of the loaded tables by query (MySQL does not check rows
    /// that were loaded while the checks were off): a child row without its parent fails the load, before the commit.
    /// After the commit, AUTO_INCREMENT counters move past the loaded values.
    /// </summary>
    public async Task EndLoadAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync("SET foreign_key_checks = 1;", null, cancellationToken).ConfigureAwait(false);
        var loaded = tables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var database = Text((await session.QueryAsync("SELECT DATABASE() AS db;", null, cancellationToken).ConfigureAwait(false))[0]["db"]);

        var keys = (await session.QueryAsync("""
            SELECT table_name AS t, constraint_name AS name, column_name AS col, referenced_table_schema AS rs, referenced_table_name AS rt, referenced_column_name AS rcol
              FROM information_schema.key_column_usage
             WHERE table_schema = DATABASE() AND referenced_table_name IS NOT NULL
             ORDER BY table_name, constraint_name, ordinal_position;
            """, null, cancellationToken).ConfigureAwait(false)).Where(r => loaded.Contains(Text(r["t"])));
        foreach (var fk in keys.GroupBy(r => (Table: Text(r["t"]), Name: Text(r["name"]))))
        {
            var parts = fk.ToList();
            var schema = Text(parts[0]["rs"]);
            var parent = (string.Equals(schema, database, StringComparison.Ordinal) ? string.Empty : MySqlModel.Q(schema) + ".") + MySqlModel.Q(Text(parts[0]["rt"]));
            var on = string.Join(" AND ", parts.Select(p => $"p.{MySqlModel.Q(Text(p["rcol"]))} = c.{MySqlModel.Q(Text(p["col"]))}"));
            var present = string.Join(" AND ", parts.Select(p => $"c.{MySqlModel.Q(Text(p["col"]))} IS NOT NULL"));
            var orphans = Convert.ToInt64((await session.QueryAsync(
                $"SELECT COUNT(*) AS n FROM {MySqlModel.Q(fk.Key.Table)} c LEFT JOIN {parent} p ON {on} WHERE p.{MySqlModel.Q(Text(parts[0]["rcol"]))} IS NULL AND {present};",
                null, cancellationToken).ConfigureAwait(false))[0]["n"], CultureInfo.InvariantCulture);
            if (orphans > 0)
            {
                throw new MigrationException(
                    $"{fk.Key.Table}: {orphans.ToString(CultureInfo.InvariantCulture)} row(s) break foreign key {fk.Key.Name} (no matching row in {Text(parts[0]["rt"])}). " +
                    "MySQL loads with foreign key checks off and does not check those rows afterwards; fix the source data and load again.");
            }
        }

        if (ownTransaction)
        {
            ownTransaction = false;
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // InnoDB moves AUTO_INCREMENT past explicit values already; set it anyway (ALTER TABLE commits implicitly).
        var identities = await session.QueryAsync(
            "SELECT table_name AS t, column_name AS col FROM information_schema.columns WHERE table_schema = DATABASE() AND extra LIKE '%auto_increment%';",
            null, cancellationToken).ConfigureAwait(false);
        foreach (var r in identities.Where(r => loaded.Contains(Text(r["t"]))))
        {
            var table = MySqlModel.Q(Text(r["t"]));
            var max = (await session.QueryAsync($"SELECT MAX({MySqlModel.Q(Text(r["col"]))}) AS m FROM {table};", null, cancellationToken).ConfigureAwait(false))[0]["m"];
            if (max is not null)
            {
                var next = Convert.ToDecimal(max, CultureInfo.InvariantCulture) + 1;
                await session.ExecuteAsync($"ALTER TABLE {table} AUTO_INCREMENT = {next.ToString(CultureInfo.InvariantCulture)};", null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>When a TIMESTAMP column is involved: sets the session time zone to UTC and returns the one to restore.</summary>
    private static async Task<string?> UtcAsync(IDatabaseSession session, IReadOnlyList<TransferColumn> columns, CancellationToken cancellationToken)
    {
        if (!columns.Any(c => BaseType(c.Type) == "timestamp"))
        {
            return null;
        }

        var zone = Text((await session.QueryAsync("SELECT @@session.time_zone AS z;", null, cancellationToken).ConfigureAwait(false))[0]["z"]);
        await session.ExecuteAsync("SET time_zone = '+00:00';", null, cancellationToken).ConfigureAwait(false);
        return zone;
    }

    /// <summary>"decimal" for "decimal(12,2)", "int" for "int unsigned".</summary>
    private static string BaseType(string type)
    {
        var end = type.IndexOfAny(['(', ' ']);
        return (end < 0 ? type : type[..end]).ToLowerInvariant();
    }

    private static string Text(object? value) => value switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
