using System.Globalization;
using System.Text.RegularExpressions;

namespace Badger.Trek.Conversion;

/// <summary>
/// SQLite stores values by affinity, not by declared type. Converting to SQLite keeps a descriptive declared type
/// (VARCHAR(50), DATETIME, BOOLEAN) whose affinity stores the values correctly; converting from SQLite reads the declared
/// type with SQLite's own affinity rules.
/// </summary>
public static partial class TypeMapper
{
    // ------------------------------------------------------------------ to SQLite

    public static TypeMapping ToSqlite(string type, ConversionContext ctx)
    {
        var t = type.Trim();
        if (t.EndsWith("[]", StringComparison.Ordinal))
        {
            return new("TEXT", "json_valid({0})", $"array {t} is stored as a JSON array in TEXT", IsText: true);
        }

        if (UserTypeRegex().Match(t) is { Success: true } user)
        {
            var schema = Unquote(user.Groups["schema"].Value);
            var typeName = Unquote(user.Groups["name"].Value);
            if (ctx.Enum(schema, typeName) is { } e)
            {
                return new("TEXT", $"{{0}} IN ({string.Join(", ", e.Labels.Select(ctx.Literal))})", null, IsText: true);
            }

            if (ctx.Domain(schema, typeName) is { } domain)
            {
                return ToSqlite(domain.BaseType, ctx);
            }

            return new("TEXT", null, $"type {t} has no SQLite counterpart; stored as text", IsText: true);
        }

        var m = TypeRegex().Match(t);
        var name = m.Groups["name"].Value.ToLowerInvariant();
        var args = m.Groups["args"].Success ? m.Groups["args"].Value.Split(',').Select(a => a.Trim()).ToArray() : [];
        var zone = m.Groups["suffix"].Value.Contains("with time zone", StringComparison.OrdinalIgnoreCase);
        string Args() => args.Length == 0 ? string.Empty : "(" + string.Join(",", args) + ")";
        var exact = "SQLite stores it as REAL when it has a fraction: exact decimals beyond 15 significant digits are rounded; keep money in integer cents if that matters";

        switch (name)
        {
            case "tinyint" when ctx.Source == Dialect.SqlServer:
                return new("INTEGER", "{0} BETWEEN 0 AND 255");
            case "smallint" or "integer" or "int" or "int4" or "bigint" or "int8" or "tinyint" or "serial" or "bigserial" or "oid":
                return new("INTEGER");
            case "bit" when ctx.Source == Dialect.SqlServer || args.Length == 0 || args[0] == "1":
            case "boolean" or "bool":
                return new("BOOLEAN", "{0} IN (0, 1)");
            case "numeric" or "decimal":
                return new("NUMERIC" + Args(), null, args.Length == 2 && args[1] != "0" ? $"{t}: {exact}" : null);
            case "money" or "smallmoney":
                return new("NUMERIC", null, $"{t}: {exact}");
            case "real" or "float4":
                return new("REAL");
            case "float" or "double precision" or "float8":
                return new("REAL");
            case "character varying" or "varchar" or "nvarchar":
                return args.Length == 0 || args[0].Equals("max", StringComparison.OrdinalIgnoreCase) ? new("TEXT", IsText: true) : new($"VARCHAR({args[0]})", IsText: true);
            case "character" or "char" or "nchar" or "bpchar":
                return new($"CHAR({(args.Length == 0 ? "1" : args[0])})", IsText: true);
            case "text" or "ntext" or "name" or "citext" or "sysname" or "\"char\"":
                return new("TEXT", IsText: true);
            case "bytea" or "binary" or "varbinary" or "image":
                return new("BLOB");
            case "timestamp" when ctx.Source == Dialect.SqlServer:
                return new("BLOB", null, "rowversion becomes BLOB: SQLite does not stamp rows on update");
            case "rowversion":
                return new("BLOB", null, "rowversion becomes BLOB: SQLite does not stamp rows on update");
            case "uuid" or "uniqueidentifier":
                return new("TEXT", null, null, IsText: true);
            case "date":
                return new("DATE");
            case "time" or "timetz":
                return new("TIME", null, zone ? "time with time zone loses its offset" : null);
            case "timestamp" or "datetime" or "datetime2" or "smalldatetime" or "timestamptz":
                return new("DATETIME", null, zone || name == "timestamptz" ? $"{t} is stored as ISO 8601 text in UTC" : null);
            case "datetimeoffset":
                return new("DATETIME", null, "datetimeoffset is stored as ISO 8601 text in UTC; the original offset is not kept");
            case "interval":
                return new("TEXT", null, "interval is stored as text; date arithmetic on it must be rewritten", IsText: true);
            case "json" or "jsonb":
                return new("TEXT", "json_valid({0})", null, IsText: true);
            case "xml":
                return new("TEXT", null, "xml is stored as text", IsText: true);
            default:
                return new("TEXT", null, $"type {t} has no SQLite counterpart; stored as text", IsText: true);
        }
    }

    // ------------------------------------------------------------------ from SQLite

    /// <summary>A SQLite declared type, read with SQLite's affinity rules plus the common declared names.</summary>
    public static TypeMapping FromSqlite(string declared, ConversionContext ctx)
    {
        var pg = ctx.Target == Dialect.PostgreSql;
        var t = declared.Trim();
        var upper = t.ToUpperInvariant();
        var m = Regex.Match(t, @"^(?<name>[A-Za-z_ ]+?)\s*(?:\((?<args>[^)]*)\))?$");
        var name = m.Success ? m.Groups["name"].Value.Trim().ToUpperInvariant() : upper;
        var args = m.Success && m.Groups["args"].Success ? m.Groups["args"].Value.Split(',').Select(a => a.Trim()).ToArray() : [];
        int Arg(int i, int fallback) => args.Length > i && int.TryParse(args[i], CultureInfo.InvariantCulture, out var v) ? v : fallback;

        if (t.Length == 0)
        {
            return new(pg ? "text" : "nvarchar(max)", null, "the column has no declared type, so it may hold values of any type; they are converted to text", IsText: true);
        }

        switch (name)
        {
            case "BOOLEAN" or "BOOL" or "BIT":
                return new(pg ? "boolean" : "bit");
            case "TINYINT" or "SMALLINT" or "INT2":
                return new("smallint");
            case "INT" or "MEDIUMINT" or "INT4":
                return new(pg ? "integer" : "int");
            case "INTEGER" or "BIGINT" or "INT8" or "UNSIGNED BIG INT":
                return new("bigint");
            case "DATE":
                return new("date");
            case "DATETIME" or "TIMESTAMP":
                return new(pg ? "timestamp" : "datetime2", null, "SQLite keeps dates as text (or numbers); values that are not ISO 8601 text fail to load");
            case "TIME":
                return new("time");
            case "UUID" or "GUID" or "UNIQUEIDENTIFIER":
                return new(pg ? "uuid" : "uniqueidentifier");
            case "JSON" or "JSONB":
                return pg ? new("jsonb") : new("nvarchar(max)", "ISJSON({0}) = 1", null, IsText: true);
            case "DECIMAL" or "NUMERIC" when args.Length > 0:
                return new(pg ? $"numeric({Arg(0, 18)},{Arg(1, 0)})" : $"decimal({Math.Min(Arg(0, 18), 38)},{Arg(1, 0)})");
            case "MONEY":
                return new(pg ? "numeric(19,4)" : "money");
            case "REAL" or "FLOAT" or "DOUBLE" or "DOUBLE PRECISION":
                return new(pg ? "double precision" : "float");
            case "VARCHAR" or "NVARCHAR" or "CHARACTER VARYING" or "VARYING CHARACTER" or "NATIVE CHARACTER" when args.Length > 0:
                return new(pg ? $"varchar({Arg(0, 255)})" : Arg(0, 255) > 4000 ? "nvarchar(max)" : $"nvarchar({Arg(0, 255)})", IsText: true);
            case "CHAR" or "NCHAR" or "CHARACTER" when args.Length > 0:
                return new(pg ? $"char({Arg(0, 1)})" : $"nchar({Math.Min(Arg(0, 1), 4000)})", IsText: true);
        }

        // SQLite's affinity rules, in its order.
        if (upper.Contains("INT", StringComparison.Ordinal))
        {
            return new("bigint");
        }

        if (upper.Contains("CHAR", StringComparison.Ordinal) || upper.Contains("CLOB", StringComparison.Ordinal) || upper.Contains("TEXT", StringComparison.Ordinal))
        {
            return new(pg ? "text" : "nvarchar(max)", IsText: true);
        }

        if (upper.Contains("BLOB", StringComparison.Ordinal))
        {
            return new(pg ? "bytea" : "varbinary(max)");
        }

        if (upper.Contains("REAL", StringComparison.Ordinal) || upper.Contains("FLOA", StringComparison.Ordinal) || upper.Contains("DOUB", StringComparison.Ordinal))
        {
            return new(pg ? "double precision" : "float");
        }

        return new(pg ? "numeric" : "decimal(38,10)", null, $"declared type {t} has NUMERIC affinity: values may be integers or reals; check the precision");
    }
}
