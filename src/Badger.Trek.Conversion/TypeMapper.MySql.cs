using System.Globalization;
using System.Text.RegularExpressions;

namespace Badger.Trek.Conversion;

/// <summary>
/// MySQL types to and from the other dialects. To MySQL: booleans are TINYINT(1), UUIDs CHAR(36), timestamps
/// DATETIME(6), unlimited text LONGTEXT, JSON stays JSON and a PostgreSQL enum becomes a MySQL ENUM. From MySQL (types as
/// information_schema.COLUMN_TYPE prints them): tinyint(1) is a boolean, unsigned integers widen to the next size so
/// every value fits, and ENUM / SET become text with a CHECK of their values.
/// </summary>
public static partial class TypeMapper
{
    // ------------------------------------------------------------------ to MySQL

    public static TypeMapping ToMySql(string type, ConversionContext ctx)
    {
        var t = type.Trim();
        if (t.EndsWith("[]", StringComparison.Ordinal))
        {
            return new("JSON", null, $"array {t} is stored as a JSON array");
        }

        if (ctx.Source == Dialect.PostgreSql && UserTypeRegex().Match(t) is { Success: true } user)
        {
            var schema = Unquote(user.Groups["schema"].Value);
            var typeName = Unquote(user.Groups["name"].Value);
            if (ctx.Enum(schema, typeName) is { } e)
            {
                return new($"ENUM({string.Join(", ", e.Labels.Select(ConversionContext.MySqlLiteral))})", null, null, IsText: true);
            }

            if (ctx.Domain(schema, typeName) is { } domain)
            {
                return ToMySql(domain.BaseType, ctx);
            }

            if (typeName.Equals("citext", StringComparison.OrdinalIgnoreCase))
            {
                return new("LONGTEXT", null, "citext becomes LONGTEXT; MySQL's default collation compares case-insensitively", IsText: true);
            }

            return new("LONGTEXT", null, $"type {t} has no MySQL counterpart; stored as text", IsText: true);
        }

        var m = MySqlTypeRegex().Match(t);
        var name = m.Groups["name"].Value.Trim().ToLowerInvariant();
        var args = m.Groups["args"].Success ? m.Groups["args"].Value.Split(',').Select(a => a.Trim()).ToArray() : [];
        var suffix = m.Groups["suffix"].Value.ToLowerInvariant();
        int Arg(int i, int fallback) => args.Length > i && int.TryParse(args[i], CultureInfo.InvariantCulture, out var v) ? v : fallback;
        bool Max() => args.Length > 0 && args[0].Equals("max", StringComparison.OrdinalIgnoreCase);
        var sqlite = ctx.Source == Dialect.Sqlite;

        switch (name)
        {
            case "tinyint" when ctx.Source == Dialect.SqlServer:
                return new("TINYINT UNSIGNED");
            case "bit" when ctx.Source == Dialect.SqlServer || args.Length == 0 || Arg(0, 1) == 1:
            case "boolean" or "bool":
                return new("TINYINT(1)");
            case "smallint" or "int2" or "smallserial" or "serial2":
                return new("SMALLINT");
            case "integer" when sqlite:
                return new("BIGINT");
            case "integer" or "int" or "int4" or "serial" or "serial4" or "mediumint":
                return new("INT");
            case "bigint" or "int8" or "bigserial" or "serial8":
                return new("BIGINT");
            case "tinyint":
                return new("TINYINT");
            case "numeric" or "decimal" when args.Length > 0:
                var precision = Arg(0, 18);
                var scale = Arg(1, 0);
                return precision > 65 || scale > 30
                    ? new($"DECIMAL({Math.Min(precision, 65)},{Math.Min(scale, 30)})", null, $"{t}: MySQL decimals have at most 65 digits and 30 decimals")
                    : new($"DECIMAL({precision},{scale})");
            case "numeric" or "decimal":
                return new("DECIMAL(65,30)", null, $"{t} without precision becomes DECIMAL(65,30); set the precision the values need");
            case "money":
                return new("DECIMAL(19,4)");
            case "smallmoney":
                return new("DECIMAL(10,4)");
            case "real" or "float4":
                return new(ctx.Source == Dialect.Sqlite ? "DOUBLE" : "FLOAT");
            case "float" when ctx.Source == Dialect.SqlServer && args.Length > 0 && Arg(0, 53) <= 24:
                return new("FLOAT");
            case "float" or "double" or "double precision" or "float8":
                return new("DOUBLE");
            case "character varying" or "varchar" or "nvarchar" or "varchar2":
                return args.Length == 0 || Max() ? new("LONGTEXT", IsText: true)
                    : Arg(0, 255) > 16383 ? new("LONGTEXT", null, $"{t} is longer than a MySQL VARCHAR holds in utf8mb4; stored as LONGTEXT", IsText: true)
                    : new($"VARCHAR({Arg(0, 255)})", IsText: true);
            case "character" or "char" or "nchar" or "bpchar":
                return Arg(0, 1) > 255 ? new($"VARCHAR({Arg(0, 1)})", IsText: true) : new($"CHAR({Arg(0, 1)})", IsText: true);
            case "text" or "ntext" or "name" or "citext" or "sysname" or "clob" or "\"char\"":
                return new("LONGTEXT", IsText: true);
            case "bytea" or "image" or "blob":
                return new("LONGBLOB");
            case "binary" when args.Length > 0:
                return new($"BINARY({Arg(0, 1)})");
            case "varbinary" when args.Length > 0 && !Max():
                return new($"VARBINARY({Arg(0, 1)})");
            case "binary" or "varbinary":
                return new("LONGBLOB");
            case "timestamp" when ctx.Source == Dialect.SqlServer:
            case "rowversion":
                return new("BINARY(8)", null, "rowversion becomes BINARY(8): MySQL does not stamp rows on update");
            case "uuid" or "uniqueidentifier" or "guid":
                return new("CHAR(36)", null, null, IsText: true);
            case "date":
                return new("DATE");
            case "time" or "timetz":
                return new($"TIME({Math.Min(Arg(0, 6), 6)})", null, suffix.Contains("with time zone", StringComparison.Ordinal) || name == "timetz" ? "time with time zone loses its offset" : null);
            case "timestamptz":
                return new($"DATETIME({Math.Min(Arg(0, 6), 6)})", null, "timestamptz is stored as DATETIME in UTC");
            case "timestamp" when suffix.Contains("with time zone", StringComparison.Ordinal):
                return new($"DATETIME({Math.Min(Arg(0, 6), 6)})", null, "timestamp with time zone is stored as DATETIME in UTC");
            case "timestamp" or "datetime2":
                return new($"DATETIME({Math.Min(Arg(0, 6), 6)})");
            case "datetime" when ctx.Source == Dialect.SqlServer:
                return new("DATETIME(3)");
            case "datetime":
                return new("DATETIME(6)");
            case "smalldatetime":
                return new("DATETIME");
            case "datetimeoffset":
                return new($"DATETIME({Math.Min(Arg(0, 6), 6)})", null, "datetimeoffset is stored as DATETIME in UTC; the original offset is not kept");
            case "interval":
                return new("VARCHAR(64)", null, "interval is stored as text; date arithmetic on it must be rewritten", IsText: true);
            case "json" or "jsonb":
                return new("JSON");
            case "xml":
                return new("LONGTEXT", null, "xml is stored as text", IsText: true);
            case "" when sqlite:
                return new("LONGTEXT", null, "the column has no declared type, so it may hold values of any type; they are converted to text", IsText: true);
        }

        if (sqlite)
        {
            // SQLite's affinity rules, in its order.
            var upper = t.ToUpperInvariant();
            if (upper.Contains("INT", StringComparison.Ordinal))
            {
                return new("BIGINT");
            }

            if (upper.Contains("CHAR", StringComparison.Ordinal) || upper.Contains("CLOB", StringComparison.Ordinal) || upper.Contains("TEXT", StringComparison.Ordinal))
            {
                return new("LONGTEXT", IsText: true);
            }

            if (upper.Contains("BLOB", StringComparison.Ordinal))
            {
                return new("LONGBLOB");
            }

            if (upper.Contains("REAL", StringComparison.Ordinal) || upper.Contains("FLOA", StringComparison.Ordinal) || upper.Contains("DOUB", StringComparison.Ordinal))
            {
                return new("DOUBLE");
            }

            return new("DECIMAL(65,30)", null, $"declared type {t} has NUMERIC affinity: values may be integers or reals; check the precision");
        }

        return new("LONGTEXT", null, $"type {t} has no MySQL counterpart; stored as text", IsText: true);
    }

    // ------------------------------------------------------------------ from MySQL

    /// <summary>A MySQL column type as information_schema.COLUMN_TYPE prints it ("int unsigned", "enum('a','b')", "tinyint(1)").</summary>
    public static TypeMapping FromMySql(string columnType, ConversionContext ctx)
    {
        var pg = ctx.Target == Dialect.PostgreSql;
        var ss = ctx.Target == Dialect.SqlServer;
        var t = columnType.Trim();
        var m = MySqlTypeRegex().Match(t);
        var name = m.Groups["name"].Value.Trim().ToLowerInvariant();
        var rawArgs = m.Groups["args"].Success ? m.Groups["args"].Value : string.Empty;
        var args = rawArgs.Length > 0 ? rawArgs.Split(',').Select(a => a.Trim()).ToArray() : [];
        var unsigned = m.Groups["suffix"].Value.Contains("unsigned", StringComparison.OrdinalIgnoreCase);
        int Arg(int i, int fallback) => args.Length > i && int.TryParse(args[i], CultureInfo.InvariantCulture, out var v) ? v : fallback;
        TypeMapping Pick(string postgres, string sqlServer, string sqlite, string? note = null, bool text = false) =>
            new(pg ? postgres : ss ? sqlServer : sqlite, null, note, text);

        switch (name)
        {
            case "tinyint" when args.Length == 1 && args[0] == "1":
            case "bool" or "boolean":
                return Pick("boolean", "bit", "BOOLEAN");
            case "tinyint":
                return unsigned ? Pick("smallint", "tinyint", "INTEGER") : Pick("smallint", "smallint", "INTEGER");
            case "smallint":
                return unsigned ? Pick("integer", "int", "INTEGER") : Pick("smallint", "smallint", "INTEGER");
            case "mediumint":
                return Pick("integer", "int", "INTEGER");
            case "int" or "integer":
                return unsigned ? Pick("bigint", "bigint", "INTEGER") : Pick("integer", "int", "INTEGER");
            case "bigint":
                return unsigned ? Pick("numeric(20,0)", "decimal(20,0)", "INTEGER", "bigint unsigned: values above 9223372036854775807 do not fit a signed bigint, so it becomes a 20-digit decimal") : Pick("bigint", "bigint", "INTEGER");
            case "decimal" or "numeric" or "dec" or "fixed":
                var precision = Arg(0, 10);
                var scale = Arg(1, 0);
                return precision > 38 && ss
                    ? new($"decimal(38,{Math.Min(scale, 38)})", null, $"{t}: SQL Server decimals have at most 38 digits")
                    : Pick($"numeric({precision},{scale})", $"decimal({precision},{scale})", $"NUMERIC({precision},{scale})", scale > 0 && !pg && !ss ? $"{t}: SQLite stores values with a fraction as REAL" : null);
            case "float":
                return Pick("real", "real", "REAL");
            case "double" or "double precision" or "real":
                return Pick("double precision", "float", "REAL");
            case "bit":
                return Arg(0, 1) == 1 ? Pick("boolean", "bit", "BOOLEAN") : Pick($"bit({Arg(0, 1)})", "bigint", "INTEGER", ss ? $"{t} is stored as a bigint" : null);
            case "char":
                return Pick($"char({Arg(0, 1)})", $"nchar({Arg(0, 1)})", $"CHAR({Arg(0, 1)})", text: true);
            case "varchar":
                return Pick($"varchar({Arg(0, 255)})", Arg(0, 255) > 4000 ? "nvarchar(max)" : $"nvarchar({Arg(0, 255)})", $"VARCHAR({Arg(0, 255)})", text: true);
            case "tinytext" or "text" or "mediumtext" or "longtext":
                return Pick("text", "nvarchar(max)", "TEXT", text: true);
            case "binary":
                return Pick("bytea", $"binary({Arg(0, 1)})", "BLOB");
            case "varbinary":
                return Pick("bytea", Arg(0, 1) > 8000 ? "varbinary(max)" : $"varbinary({Arg(0, 1)})", "BLOB");
            case "tinyblob" or "blob" or "mediumblob" or "longblob":
                return Pick("bytea", "varbinary(max)", "BLOB");
            case "date":
                return Pick("date", "date", "DATE");
            case "datetime":
                return Pick($"timestamp({Arg(0, 0)})", $"datetime2({Arg(0, 0)})", "DATETIME");
            case "timestamp":
                return Pick($"timestamp({Arg(0, 0)}) with time zone", $"datetimeoffset({Arg(0, 0)})", "DATETIME", "MySQL TIMESTAMP is stored in UTC and shown in the session time zone; it becomes a time-zone-aware type");
            case "time":
                return Pick($"time({Arg(0, 0)})", $"time({Arg(0, 0)})", "TIME", "MySQL TIME also holds durations up to 838 hours; those do not fit a time of day");
            case "year":
                return Pick("smallint", "smallint", "INTEGER");
            case "json":
                return pg ? new("jsonb") : ss ? new("nvarchar(max)", "ISJSON({0}) = 1", null, true) : new("TEXT", "json_valid({0})", null, true);
            case "enum" or "set":
                var labels = EnumLabels(rawArgs);
                var length = Math.Max(1, labels.Count == 0 ? 1 : name == "set" ? labels.Sum(l => l.Length + 1) : labels.Max(l => l.Length));
                var check = name == "enum" ? $"{{0}} IN ({string.Join(", ", labels.Select(ctx.Literal))})" : null;
                return new(pg ? $"varchar({length})" : ss ? $"nvarchar({length})" : "TEXT", check, name == "set" ? $"{t}: a SET becomes comma-separated text" : null, true);
            case "geometry" or "point" or "linestring" or "polygon" or "multipoint" or "multilinestring" or "multipolygon" or "geometrycollection":
                return Pick("text", "nvarchar(max)", "TEXT", $"{t} is stored as text (WKT); spatial types are not converted", true);
        }

        return Pick("text", "nvarchar(max)", "TEXT", $"type {t} has no counterpart; stored as text", true);
    }

    /// <summary>The labels of "'a','b','it''s'" as MySQL prints ENUM and SET values.</summary>
    internal static List<string> EnumLabels(string args)
    {
        var labels = new List<string>();
        foreach (Match m in EnumLabelRegex().Matches(args))
        {
            labels.Add(m.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal));
        }

        return labels;
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z_ \x22]+?)\s*(?:\((?<args>.*)\))?(?<suffix>(?:\s+(?:unsigned|zerofill|with(?:out)?\s+time\s+zone))*)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MySqlTypeRegex();

    [GeneratedRegex(@"'((?:[^'\\]|''|\\.)*)'")]
    private static partial Regex EnumLabelRegex();
}
