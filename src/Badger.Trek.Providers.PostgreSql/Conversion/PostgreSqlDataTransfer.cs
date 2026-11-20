using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Badger.Trek.Core.Conversion;
using Badger.Trek.Core.Providers;
using Badger.Trek.Providers.PostgreSql.Scripting;
using Npgsql;

namespace Badger.Trek.Providers.PostgreSql.Conversion;

/// <summary>
/// Reads tables with a streaming reader (arrays as JSON text; enums, intervals, network and other non-standard types as
/// text) and loads them with COPY in text format, so PostgreSQL parses each value with the column's own type (enums,
/// domains and arrays included). The load runs in one transaction with foreign keys deferred and user triggers off.
/// </summary>
public sealed class PostgreSqlDataTransfer : IDataTransfer
{
    private static readonly HashSet<string> Native = new(StringComparer.Ordinal)
    {
        "smallint", "integer", "bigint", "numeric", "real", "double precision", "boolean", "text", "character varying", "character", "bytea", "uuid", "date",
        "timestamp without time zone", "timestamp with time zone", "time without time zone", "json", "jsonb", "xml",
    };

    private static NpgsqlConnection Connection(IDatabaseSession session) =>
        (session as PostgreSqlSession ?? throw new ArgumentException("Not a PostgreSQL session.", nameof(session))).Connection;

    private static string Name(PortableName table) => $"{Sql.Ident(table.Schema)}.{Sql.Ident(table.Name)}";

    public async Task<IReadOnlyList<TransferColumn>> ColumnsAsync(IDatabaseSession session, PortableName table, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync("""
            SELECT a.attname, format_type(a.atttypid, NULL) AS type, t.typcategory::text AS category
            FROM pg_attribute a
            JOIN pg_type t ON t.oid = a.atttypid
            WHERE a.attrelid = to_regclass(@name) AND a.attnum > 0 AND NOT a.attisdropped AND a.attgenerated = ''
            ORDER BY a.attnum;
            """, new Dictionary<string, object?> { ["name"] = Name(table) }, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new TransferColumn((string)r["attname"]!, (string)r["category"]! == "A" ? (string)r["type"]! : (string)r["type"]!)).ToList();
    }

    public async Task<long> CountAsync(IDatabaseSession session, PortableName table, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT count(*) AS n FROM {Name(table)};", null, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture);
    }

    public async IAsyncEnumerable<object?[]> ReadAsync(IDatabaseSession session, PortableName table, IReadOnlyList<TransferColumn> columns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var select = string.Join(", ", columns.Select(c => c.Type switch
        {
            _ when c.Type.EndsWith("[]", StringComparison.Ordinal) => $"to_json({Sql.Ident(c.Name)})::text",
            "money" => $"{Sql.Ident(c.Name)}::numeric",
            "time with time zone" => $"{Sql.Ident(c.Name)}::time",
            _ when Native.Contains(c.Type) => Sql.Ident(c.Name),
            _ => $"{Sql.Ident(c.Name)}::text",
        }));
        await using var command = new NpgsqlCommand($"SELECT {select} FROM {Name(table)};", Connection(session)) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false) ? null : reader.GetValue(i);
            }

            yield return row;
        }
    }

    public async Task<long> WriteAsync(IDatabaseSession session, PortableName table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken cancellationToken)
    {
        long written = 0;
        var copy = $"COPY {Name(table)} ({string.Join(", ", columns.Select(c => Sql.Ident(c.Name)))}) FROM STDIN (FORMAT text)";
        await using (var writer = await Connection(session).BeginTextImportAsync(copy, cancellationToken).ConfigureAwait(false))
        {
            var line = new StringBuilder();
            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                line.Clear();
                for (var i = 0; i < row.Length; i++)
                {
                    if (i > 0)
                    {
                        line.Append('\t');
                    }

                    Append(line, row[i], columns[i].Type);
                }

                line.Append('\n');
                await writer.WriteAsync(line.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
                written++;
            }
        }

        return written;
    }

    /// <summary>A value in COPY text format: \N for NULL, backslash escapes, PostgreSQL input syntax.</summary>
    private static void Append(StringBuilder sb, object? value, string type)
    {
        string text;
        switch (value)
        {
            case null:
                sb.Append("\\N");
                return;
            case bool b:
                text = type is "boolean" ? (b ? "t" : "f") : (b ? "1" : "0");
                break;
            case byte[] bytes:
                text = "\\x" + Convert.ToHexString(bytes);
                break;
            case DateTime d:
                text = d.ToString(d.Kind == DateTimeKind.Utc ? "yyyy-MM-dd HH:mm:ss.ffffff'+00'" : "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                break;
            case DateTimeOffset o:
                text = o.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
                break;
            case DateOnly d:
                text = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case TimeSpan t when type.StartsWith("time", StringComparison.Ordinal):
                text = t.ToString(@"hh\:mm\:ss\.ffffff", CultureInfo.InvariantCulture);
                break;
            case TimeOnly t:
                text = t.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                break;
            case Guid g:
                text = g.ToString();
                break;
            case IFormattable f:
                text = f.ToString(null, CultureInfo.InvariantCulture);
                break;
            case IEnumerable e and not string:
                text = System.Text.Json.JsonSerializer.Serialize(e);
                break;
            default:
                text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                break;
        }

        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }

    public async Task TruncateAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        if (tables.Count > 0)
        {
            await session.ExecuteAsync($"TRUNCATE {string.Join(", ", tables.Select(Name))};", null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task BeginLoadAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await session.ExecuteAsync("SET CONSTRAINTS ALL DEFERRED;", null, cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            await session.ExecuteAsync($"ALTER TABLE {Name(table)} DISABLE TRIGGER USER;", null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task EndLoadAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        // Check the deferred foreign keys now (a row without its parent fails the load and rolls it back); triggers
        // cannot be turned back on while those checks are pending.
        await session.ExecuteAsync("SET CONSTRAINTS ALL IMMEDIATE;", null, cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            await session.ExecuteAsync($"ALTER TABLE {Name(table)} ENABLE TRIGGER USER;", null, cancellationToken).ConfigureAwait(false);

            // Identity and serial sequences: move them past the loaded values.
            var sequences = await session.QueryAsync("""
                SELECT a.attname, pg_get_serial_sequence(@name, a.attname) AS seq
                FROM pg_attribute a
                WHERE a.attrelid = to_regclass(@name) AND a.attnum > 0 AND NOT a.attisdropped AND pg_get_serial_sequence(@name, a.attname) IS NOT NULL;
                """, new Dictionary<string, object?> { ["name"] = Name(table) }, cancellationToken).ConfigureAwait(false);
            foreach (var s in sequences)
            {
                await session.QueryAsync($"SELECT setval(@seq, COALESCE(max({Sql.Ident((string)s["attname"]!)}), 1), max({Sql.Ident((string)s["attname"]!)}) IS NOT NULL) AS v FROM {Name(table)};",
                    new Dictionary<string, object?> { ["seq"] = s["seq"] }, cancellationToken).ConfigureAwait(false);
            }
        }

        // Sequences behind column defaults (DEFAULT nextval('seq')): move them past the loaded values too.
        foreach (var table in tables)
        {
            var defaults = await session.QueryAsync("""
                SELECT a.attname, d.refobjid::regclass::text AS seq
                FROM pg_attrdef ad
                JOIN pg_depend d ON d.objid = ad.oid AND d.classid = 'pg_attrdef'::regclass AND d.refclassid = 'pg_class'::regclass
                JOIN pg_class s ON s.oid = d.refobjid AND s.relkind = 'S'
                JOIN pg_attribute a ON a.attrelid = ad.adrelid AND a.attnum = ad.adnum
                WHERE ad.adrelid = to_regclass(@name);
                """, new Dictionary<string, object?> { ["name"] = Name(table) }, cancellationToken).ConfigureAwait(false);
            foreach (var d in defaults)
            {
                var column = Sql.Ident((string)d["attname"]!);
                await session.QueryAsync($"SELECT setval(CAST(@seq AS regclass), GREATEST(COALESCE(pg_sequence_last_value(CAST(@seq AS regclass)), 0), max({column}))) AS v FROM {Name(table)} HAVING max({column}) IS NOT NULL;",
                    new Dictionary<string, object?> { ["seq"] = d["seq"] }, cancellationToken).ConfigureAwait(false);
            }
        }

        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
