using System.Globalization;
using System.Text.RegularExpressions;

namespace Badger.Trek.Conversion;

/// <param name="Type">The target type as written in DDL.</param>
/// <param name="Check">A CHECK the target needs to keep the source's rules ({0} = the quoted column), or null.</param>
/// <param name="Note">What is lost or approximated, or null when the mapping is exact.</param>
/// <param name="IsText">A character type (gets the case-insensitive collation on PostgreSQL).</param>
public sealed record TypeMapping(string Type, string? Check = null, string? Note = null, bool IsText = false);

/// <summary>Maps column types between PostgreSQL, SQL Server, SQLite and MySQL.</summary>
public static partial class TypeMapper
{
    public static TypeMapping Map(string sourceType, ConversionContext ctx) => (ctx.Source, ctx.Target) switch
    {
        (Dialect.MySql, Dialect.MySql) => BetweenMySqlAndMariaDb(sourceType, ctx),
        (Dialect.MySql, _) => FromMySql(sourceType, ctx),
        (_, Dialect.MySql) => ToMySql(sourceType, ctx),
        (_, Dialect.Sqlite) => ToSqlite(sourceType, ctx),
        (Dialect.Sqlite, _) => FromSqlite(sourceType, ctx),
        (Dialect.PostgreSql, _) => PgToTSql(sourceType, ctx),
        _ => TSqlToPg(sourceType, ctx),
    };

    // ------------------------------------------------------------------ PostgreSQL → SQL Server

    public static TypeMapping PgToTSql(string type, ConversionContext ctx)
    {
        var t = type.Trim();
        if (t.EndsWith("[]", StringComparison.Ordinal))
        {
            return new("nvarchar(max)", "ISJSON({0}) = 1", $"array {t} is stored as a JSON array in nvarchar(max)", IsText: true);
        }

        var m = TypeRegex().Match(t);
        var name = m.Groups["name"].Value.ToLowerInvariant();
        var args = m.Groups["args"].Success ? m.Groups["args"].Value.Split(',').Select(a => a.Trim()).ToArray() : [];
        var suffix = m.Groups["suffix"].Value.ToLowerInvariant();
        int Arg(int i, int fallback) => args.Length > i && int.TryParse(args[i], CultureInfo.InvariantCulture, out var v) ? v : fallback;

        // User types: schema.name
        if (UserTypeRegex().Match(t) is { Success: true } user)
        {
            var schema = Unquote(user.Groups["schema"].Value);
            var typeName = Unquote(user.Groups["name"].Value);
            if (ctx.Enum(schema, typeName) is { } e)
            {
                var length = Math.Max(1, e.Labels.Count == 0 ? 1 : e.Labels.Max(l => l.Length));
                return new($"nvarchar({length})", $"{{0}} IN ({string.Join(", ", e.Labels.Select(l => ctx.Literal(l)))})", null, IsText: true);
            }

            if (ctx.Domain(schema, typeName) is not null)
            {
                return new(ctx.QualifiedTarget(ctx.Names.Schema(schema), ctx.Names.Name(typeName)));
            }

            if (typeName.Equals("citext", StringComparison.OrdinalIgnoreCase))
            {
                return new("nvarchar(max)", null, "citext becomes nvarchar(max); the database's case-insensitive collation keeps comparisons case-insensitive", IsText: true);
            }

            return new("nvarchar(max)", null, $"type {t} has no SQL Server counterpart; stored as text", IsText: true);
        }

        switch (name)
        {
            case "smallint": return new("smallint");
            case "integer": case "int": case "int4": return new("int");
            case "bigint": case "int8": return new("bigint");
            case "real": case "float4": return new("real");
            case "double precision": case "float8": return new("float");
            case "money": return new("money");
            case "boolean": case "bool": return new("bit");
            case "numeric":
            case "decimal":
                if (args.Length == 0)
                {
                    return new("decimal(38,10)", null, "numeric without a precision becomes decimal(38,10): values with more than 28 integer digits or 10 decimals do not fit");
                }

                return Arg(0, 38) > 38
                    ? new("decimal(38," + Math.Min(Arg(1, 0), 38) + ")", null, $"numeric({string.Join(',', args)}) is wider than SQL Server's maximum precision 38")
                    : new($"decimal({Arg(0, 18)},{Arg(1, 0)})");
            case "character varying":
            case "varchar":
                return args.Length == 0 || Arg(0, 4001) > 4000 ? new("nvarchar(max)", IsText: true) : new($"nvarchar({Arg(0, 1)})", IsText: true);
            case "character":
            case "char":
            case "bpchar":
                return Arg(0, 1) > 4000 ? new("nvarchar(max)", IsText: true) : new($"nchar({Arg(0, 1)})", IsText: true);
            case "\"char\"": return new("nchar(1)", IsText: true);
            case "text": return new("nvarchar(max)", IsText: true);
            case "name": return new("nvarchar(63)", IsText: true);
            case "bytea": return new("varbinary(max)");
            case "uuid": return new("uniqueidentifier");
            case "date": return new("date");
            case "time":
                return suffix.Contains("with time zone", StringComparison.Ordinal)
                    ? new($"time({Math.Min(Arg(0, 6), 7)})", null, "time with time zone loses its offset (SQL Server's time has none)")
                    : new($"time({Math.Min(Arg(0, 6), 7)})");
            case "timestamp":
                return suffix.Contains("with time zone", StringComparison.Ordinal)
                    ? new($"datetimeoffset({Math.Min(Arg(0, 6), 7)})")
                    : new($"datetime2({Math.Min(Arg(0, 6), 7)})");
            case "interval": return new("nvarchar(100)", null, "interval is stored as text (e.g. '1 day 02:00:00'); date arithmetic on it must be rewritten", IsText: true);
            case "json":
            case "jsonb":
                return new("nvarchar(max)", "ISJSON({0}) = 1", name == "jsonb" ? "jsonb is stored as JSON text in nvarchar(max); jsonb operators (->, ->>, @>) must be rewritten with JSON_VALUE/OPENJSON" : null, IsText: true);
            case "xml": return new("xml");
            case "inet": case "cidr": return new("nvarchar(43)", null, $"{name} is stored as text", IsText: true);
            case "macaddr": case "macaddr8": return new("nvarchar(23)", null, $"{name} is stored as text", IsText: true);
            case "bit":
                return Arg(0, 1) == 1 ? new("bit") : new($"varchar({Arg(0, 1)})", null, $"bit({Arg(0, 1)}) is stored as a string of 0s and 1s");
            case "bit varying": case "varbit": return new("varchar(max)", null, "bit varying is stored as a string of 0s and 1s");
            case "oid": return new("bigint");
            case "tsvector":
            case "tsquery":
                return new("nvarchar(max)", null, $"{name} is stored as text; use SQL Server full-text search instead", IsText: true);
            case "point": case "line": case "lseg": case "box": case "path": case "polygon": case "circle":
                return new("nvarchar(max)", null, $"geometric type {name} is stored as text; consider geometry", IsText: true);
            default:
                return new("nvarchar(max)", null, $"type {t} has no SQL Server counterpart; stored as text", IsText: true);
        }
    }

    // ------------------------------------------------------------------ SQL Server → PostgreSQL

    public static TypeMapping TSqlToPg(string type, ConversionContext ctx)
    {
        var t = type.Trim();
        if (UserTypeRegex().Match(t) is { Success: true } user)
        {
            var schema = Unquote(user.Groups["schema"].Value);
            var name = Unquote(user.Groups["name"].Value);
            if (ctx.Domain(schema, name) is { } domain)
            {
                var underlying = TSqlToPg(domain.BaseType, ctx);
                return underlying with { Type = ctx.QualifiedTarget(ctx.Names.Schema(schema), ctx.Names.Name(name)), Check = null };
            }

            return new("text", null, $"type {t} has no PostgreSQL counterpart; stored as text", IsText: true);
        }

        var m = TypeRegex().Match(t);
        var n = m.Groups["name"].Value.ToLowerInvariant();
        var args = m.Groups["args"].Success ? m.Groups["args"].Value.Split(',').Select(a => a.Trim()).ToArray() : [];
        var max = args.Length == 1 && args[0].Equals("max", StringComparison.OrdinalIgnoreCase);
        int Arg(int i, int fallback) => args.Length > i && int.TryParse(args[i], CultureInfo.InvariantCulture, out var v) ? v : fallback;

        switch (n)
        {
            case "bigint": return new("bigint");
            case "int": return new("integer");
            case "smallint": return new("smallint");
            case "tinyint": return new("smallint", "{0} BETWEEN 0 AND 255");
            case "bit": return new("boolean");
            case "decimal": case "numeric": return new($"numeric({Arg(0, 18)},{Arg(1, 0)})");
            case "money": return new("numeric(19,4)");
            case "smallmoney": return new("numeric(10,4)");
            case "float": return Arg(0, 53) <= 24 ? new("real") : new("double precision");
            case "real": return new("real");
            case "date": return new("date");
            case "time": return new($"time({Math.Min(Arg(0, 7), 6)})", null, Arg(0, 7) > 6 ? "time(7) becomes time(6): PostgreSQL keeps microseconds, not 100 ns ticks" : null);
            case "datetime": return new("timestamp(3)");
            case "smalldatetime": return new("timestamp(0)");
            case "datetime2": return new($"timestamp({Math.Min(Arg(0, 7), 6)})", null, Arg(0, 7) > 6 ? "datetime2(7) becomes timestamp(6): PostgreSQL keeps microseconds, not 100 ns ticks" : null);
            case "datetimeoffset": return new($"timestamptz({Math.Min(Arg(0, 7), 6)})", null, "datetimeoffset becomes timestamptz: values keep their instant, but not the offset they were written with");
            case "char": return new($"char({Arg(0, 1)})", IsText: true);
            case "nchar": return new($"char({Arg(0, 1)})", IsText: true);
            case "varchar": case "nvarchar": return max ? new("text", IsText: true) : new($"varchar({Arg(0, 1)})", IsText: true);
            case "sysname": return new("varchar(128)", IsText: true);
            case "text": case "ntext": return new("text", IsText: true);
            case "binary": case "varbinary": case "image": return new("bytea");
            case "uniqueidentifier": return new("uuid");
            case "xml": return new("xml");
            case "json": return new("jsonb");
            case "timestamp":
            case "rowversion":
                return new("bytea", null, "rowversion becomes bytea: PostgreSQL does not stamp rows on update; use xmin or a trigger for optimistic concurrency");
            case "sql_variant": return new("text", null, "sql_variant is stored as text", IsText: true);
            case "hierarchyid": return new("text", null, "hierarchyid is stored as its string form (/1/2/); consider ltree", IsText: true);
            case "geography": case "geometry": return new("text", null, $"{n} is stored as WKT text; consider PostGIS", IsText: true);
            case "vector": return new("text", null, "vector is stored as text; consider pgvector", IsText: true);
            default: return new("text", null, $"type {t} has no PostgreSQL counterpart; stored as text", IsText: true);
        }
    }

    private static string Unquote(string name) =>
        name.Length >= 2 && ((name[0] == '"' && name[^1] == '"') || (name[0] == '[' && name[^1] == ']'))
            ? name[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal).Replace("]]", "]", StringComparison.Ordinal)
            : name;

    [GeneratedRegex("""^(?<name>"char"|[A-Za-z_][A-Za-z0-9_ ]*?)\s*(?:\((?<args>[^)]*)\))?(?<suffix>(?:\s+with(?:out)?\s+time\s+zone)?)$""", RegexOptions.IgnoreCase)]
    private static partial Regex TypeRegex();

    [GeneratedRegex("""^(?<schema>"(?:[^"]|"")+"|\[(?:[^\]]|\]\])+\]|[A-Za-z_][A-Za-z0-9_$]*)\.(?<name>"(?:[^"]|"")+"|\[(?:[^\]]|\]\])+\]|[A-Za-z_][A-Za-z0-9_$]*)$""")]
    private static partial Regex UserTypeRegex();
}
