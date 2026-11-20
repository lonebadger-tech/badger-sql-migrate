using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.Sqlite;

/// <summary>
/// The MySQL side of the printer: PostgreSQL written as MySQL (functions, casts, intervals, JSON paths, NULL ordering),
/// and MySQL read in MySQL mode written as PostgreSQL. MySQL differs from PostgreSQL in ways that silently change
/// results (CONCAT and NULL, / on integers, NULL sort order, case-insensitive comparison), so those are rewritten
/// explicitly; what cannot be rewritten faithfully is kept with a REVIEW comment.
/// </summary>
internal sealed partial class SqlPrinter
{
    /// <summary>
    /// MySQL compares text case-insensitively under its default collations (utf8mb4_0900_ai_ci): LIKE and REGEXP too.
    /// Only a source whose collation is binary or case-sensitive (_bin, _cs) compares exactly.
    /// </summary>
    private bool MySqlCaseInsensitive =>
        ctx.Schema.Collation is not { } collation
        || !(collation.EndsWith("_bin", StringComparison.OrdinalIgnoreCase) || collation.EndsWith("_cs", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ column knowledge

    private List<PortableColumn> ColumnsNamed(string name) =>
        [.. ctx.Schema.Tables.SelectMany(t => t.Columns).Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The expression is a column, and every source column of that name has a type that passes the test.</summary>
    private bool ColumnTypes(PgExpr e, Func<string, bool> test) =>
        Unparen(e) is PgName n && ColumnsNamed(n.Last) is { Count: > 0 } columns && columns.All(c => test(c.Type.Trim().ToLowerInvariant()));

    private bool IsIntegerType(string type)
    {
        var t = type.Replace(" unsigned", string.Empty, StringComparison.Ordinal).Trim();
        var name = t.Split('(')[0].Trim();
        return ctx.Source == Dialect.MySql
            ? name is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "signed" && !t.StartsWith("tinyint(1)", StringComparison.Ordinal)
            : name is "smallint" or "integer" or "int" or "bigint" or "int2" or "int4" or "int8" or "serial" or "bigserial" or "smallserial" or "signed";
    }

    /// <summary>An integer-valued expression, as far as can be told without a type checker (literals, integer columns, count()).</summary>
    private bool IsInteger(PgExpr e) => Unparen(e) switch
    {
        PgLiteral { Kind: LiteralKind.Number } n => !n.Value.Contains('.', StringComparison.Ordinal) && !n.Value.Contains('e', StringComparison.OrdinalIgnoreCase),
        PgCast c => IsIntegerType(c.Type.ToLowerInvariant()),
        PgName n => ColumnTypes(n, IsIntegerType),
        PgCall { Function.Last: var f } => f.Equals("count", StringComparison.OrdinalIgnoreCase),
        PgUnary { Op: "-" or "+" } u => IsInteger(u.Operand),
        PgBinary { Op: "+" or "-" or "*" or "%" } b => IsInteger(b.Left) && IsInteger(b.Right),
        _ => false,
    };

    private bool IsDate(PgExpr e) => IsDateLike(e) || ColumnTypes(e, t => t == "date");

    private bool IsTimestamp(PgExpr e) => Unparen(e) switch
    {
        PgCast c => c.Type.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase),
        PgTypedLiteral t => t.Type.StartsWith("timestamp", StringComparison.Ordinal),
        PgCall c => c.Function.Last is "now" or "current_timestamp" or "localtimestamp" or "clock_timestamp" or "transaction_timestamp" or "statement_timestamp",
        var other => ColumnTypes(other, t => t.StartsWith("timestamp", StringComparison.Ordinal) || t.StartsWith("datetime", StringComparison.Ordinal)),
    };

    /// <summary>The value can be NULL: false only for literals, count() and columns that are NOT NULL wherever they appear.</summary>
    private bool MaybeNull(PgExpr e) => Unparen(e) switch
    {
        PgLiteral { Kind: not LiteralKind.Null } => false,
        PgCall { Function.Last: var f } when f.Equals("count", StringComparison.OrdinalIgnoreCase) => false,
        PgName n => ColumnsNamed(n.Last) is not { Count: > 0 } columns || columns.Any(c => c.Nullable),
        _ => true,
    };

    private bool IsBooleanColumn(PgExpr e) => Unparen(e) is PgName n && ctx.ColumnKindOf(n.Last) == ConversionContext.ColumnKind.Boolean;

    /// <summary>
    /// NULL ordering: PostgreSQL sorts NULLs last ascending and first descending, MySQL the other way round, and MySQL
    /// has no NULLS FIRST / LAST. To MySQL, a sort that needs NULLs elsewhere sorts on IS NULL first; to PostgreSQL,
    /// MySQL's order is written out with NULLS FIRST / LAST.
    /// </summary>
    private string MySqlOrderTerm(PgOrder o, string term)
    {
        if (fromMySql)
        {
            return o.Nulls is not null ? term + (o.Nulls.Equals("last", StringComparison.OrdinalIgnoreCase) ? " NULLS LAST" : " NULLS FIRST")
                : MaybeNull(o.Expr) ? term + (o.Descending ? " NULLS LAST" : " NULLS FIRST")
                : term;
        }

        var wantLast = o.Nulls is null ? !o.Descending : o.Nulls.Equals("last", StringComparison.OrdinalIgnoreCase);
        var mysqlLast = o.Descending;
        return wantLast == mysqlLast || !MaybeNull(o.Expr) ? term : $"{Expr(o.Expr)} IS {(wantLast ? string.Empty : "NOT ")}NULL, {term}";
    }

    // ------------------------------------------------------------------ PostgreSQL → MySQL: operators

    private string LikeToMySql(PgLike l)
    {
        var not = l.Not ? "NOT " : string.Empty;
        var escape = l.Escape is null ? string.Empty : $" ESCAPE {Expr(l.Escape)}";
        switch (l.Op)
        {
            case "like":
                return $"{Expr(l.Operand)} {not}LIKE {Expr(l.Pattern)}{escape}"
                    + (ctx.Schema.CaseInsensitive ? string.Empty : Note($"{My}'s LIKE ignores case under its default collations"));
            case "ilike":
                return $"{Expr(l.Operand)} {not}LIKE {Expr(l.Pattern)}{escape}";
            case "~" or "~*" when toMariaDb:
                // MariaDB has no REGEXP_LIKE, and its REGEXP (PCRE) follows the collation: a (?-i) or (?i) at the start
                // of the pattern says which PostgreSQL meant.
                return $"{Expr(l.Operand)} {not}REGEXP {PcrePattern(l.Pattern, l.Op == "~*")}";
            case "~" or "~*":
                // REGEXP alone would follow the collation; the match type says which PostgreSQL meant.
                return $"{not}REGEXP_LIKE({Expr(l.Operand)}, {Expr(l.Pattern)}, '{(l.Op == "~" ? "c" : "i")}')";
            case "regexp":
                return $"{Expr(l.Operand)} {not}REGEXP {Expr(l.Pattern)}";
            default:
                return Unsupported($"{l.Op.ToUpperInvariant()} has no {My} counterpart", $"{Show(l.Operand)} {l.Op} {Show(l.Pattern)}", "TRUE");
        }
    }

    /// <summary>
    /// A MariaDB REGEXP pattern with PCRE's case flag in front: (?i) ignores case, (?-i) does not, whatever the
    /// collation of the operands says.
    /// </summary>
    private string PcrePattern(PgExpr pattern, bool insensitive)
    {
        var flag = insensitive ? "(?i)" : "(?-i)";
        return StringArg(pattern) is { } literal ? Lit(flag + literal) : $"CONCAT('{flag}', {Expr(pattern)})";
    }

    private string BinaryToMySql(PgBinary b)
    {
        if (b.Op is "+" or "-" && MySqlIntervalParts(b.Right) is { } right)
        {
            return Expr(b.Left) + IntervalText(right, b.Op == "-");
        }

        if (b.Op == "+" && MySqlIntervalParts(b.Left) is { } left)
        {
            return Expr(b.Right) + IntervalText(left, false);
        }

        if (b.Op is "+" or "-" && (IsIntervalValue(b.Left) || IsIntervalValue(b.Right)))
        {
            return Unsupported("interval arithmetic not translated (only literal intervals and n * interval are)", Show(b));
        }

        if (b.Op == "-" && IsDate(b.Left) && IsDate(b.Right))
        {
            return $"DATEDIFF({Expr(b.Left)}, {Expr(b.Right)})";
        }

        if (b.Op == "-" && IsTimestamp(b.Left) && IsTimestamp(b.Right))
        {
            return Unsupported($"subtracting timestamps gives an interval in PostgreSQL; in {My} use TIMESTAMPDIFF()", Show(b));
        }

        if (b.Op is "->" or "->>" or "#>" or "#>>")
        {
            return JsonToMySql(b);
        }

        if (b.Op == "||")
        {
            // Like ||, CONCAT() is NULL when any argument is.
            return $"CONCAT({string.Join(", ", ConcatParts(b).Select(Expr))})";
        }

        return b.Op switch
        {
            "and" or "or" => $"{Expr(b.Left)} {b.Op.ToUpperInvariant()} {Expr(b.Right)}",

            // PostgreSQL divides integers as integers; MySQL's / always gives a decimal.
            "/" when IsInteger(b.Left) && IsInteger(b.Right) => $"{Expr(b.Left)} DIV {Expr(b.Right)}",
            "^" => $"POW({Expr(b.Left)}, {Expr(b.Right)})",
            "#" => $"{Expr(b.Left)} ^ {Expr(b.Right)}",
            "@>" or "<@" or "?" or "?|" or "?&" or "&&" or "@@" or "-|-" => Unsupported($"operator {b.Op} has no {My} counterpart", Show(b)),
            _ => $"{Expr(b.Left)} {b.Op} {Expr(b.Right)}",
        };
    }

    /// <summary>The operands of a chain of || (pg_get_expr writes ((a || b) || c)), for one CONCAT().</summary>
    private static IEnumerable<PgExpr> ConcatParts(PgExpr e) =>
        Unparen(e) is PgBinary { Op: "||" } b ? ConcatParts(b.Left).Concat(ConcatParts(b.Right)) : [e];

    private static bool IsIntervalValue(PgExpr e) => Unparen(e) is PgTypedLiteral { Type: "interval" } or PgCast { Type: "interval" };

    /// <summary>PostgreSQL's JSON operators as MySQL JSON paths: doc ->> 'name' is doc ->> '$.name'; a chain becomes one path.</summary>
    private string JsonToMySql(PgBinary b)
    {
        var path = new List<string>();
        PgExpr root = b;
        while (Unparen(root) is PgBinary { Op: "->" or "->>" or "#>" or "#>>" } step
               && (ReferenceEquals(step, b) || step.Op is "->" or "#>")
               && JsonSegments(step) is { } segments)
        {
            path.InsertRange(0, segments);
            root = step.Left;
        }

        var text = b.Op is "->>" or "#>>";
        if (ReferenceEquals(root, b))
        {
            if (b.Op is "#>" or "#>>")
            {
                return Unsupported("JSON path operator with a path that is not a literal", Show(b));
            }

            // A key only known at run time: taken as an object key.
            var extract = $"JSON_EXTRACT({Expr(b.Left)}, CONCAT('$.\"', {Expr(b.Right)}, '\"'))";
            return text ? $"JSON_UNQUOTE({extract})" : extract;
        }

        var jsonPath = Lit("$" + string.Concat(path));

        // MySQL's -> and ->> take a column on the left; anything else goes through JSON_EXTRACT. MariaDB has neither operator.
        return Unparen(root) is PgName && !toMariaDb
            ? $"{Expr(root)} {(text ? "->>" : "->")} {jsonPath}"
            : text ? $"JSON_UNQUOTE(JSON_EXTRACT({Expr(root)}, {jsonPath}))" : $"JSON_EXTRACT({Expr(root)}, {jsonPath})";
    }

    /// <summary>An interval as MySQL INTERVAL parts (amount, unit): interval '2 hours', '1 day'::interval, n * interval '1 day'.</summary>
    private List<(string Amount, string Unit)>? MySqlIntervalParts(PgExpr e)
    {
        switch (Unparen(e))
        {
            case PgTypedLiteral { Type: "interval" } t:
                return ParseIntervalText(t.Value);
            case PgCast { Type: "interval", Operand: PgLiteral { Kind: LiteralKind.String } s }:
                return ParseIntervalText(s.Value);
            case PgBinary { Op: "*" } m:
            {
                var (interval, factor) = MySqlIntervalParts(m.Right) is { Count: 1 } r ? (r, m.Left) : MySqlIntervalParts(m.Left) is { Count: 1 } l ? (l, m.Right) : (null, null);
                if (interval is null)
                {
                    return null;
                }

                var amount = interval[0].Amount == "1" ? $"({Expr(factor!)})" : $"({Expr(factor!)}) * {interval[0].Amount}";
                return [(amount, interval[0].Unit)];
            }

            default:
                return null;
        }
    }

    private static string IntervalText(List<(string Amount, string Unit)> parts, bool negate) =>
        string.Concat(parts.Select(p => $" {(negate ? "-" : "+")} INTERVAL {p.Amount} {p.Unit}"));

    /// <summary>
    /// A PostgreSQL interval text ('1 day', '2 years 3 mons', '02:30:00') as MySQL units, or null when any part has no
    /// exact MySQL form (a fraction of a day, a decade).
    /// </summary>
    private static List<(string Amount, string Unit)>? ParseIntervalText(string text)
    {
        var parts = new List<(string, string)>();
        var at = 0;
        var s = text.Trim().ToLowerInvariant();
        while (at < s.Length)
        {
            var clock = Regex.Match(s[at..], @"\A(?<sign>[-+]?)(?<h>\d+):(?<m>\d+)(?::(?<s>\d+(?:\.\d+)?))?\s*");
            if (clock.Success)
            {
                var sign = clock.Groups["sign"].Value == "-" ? "-" : string.Empty;
                parts.Add((sign + int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), "HOUR"));
                parts.Add((sign + int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), "MINUTE"));
                if (clock.Groups["s"].Success)
                {
                    parts.Add((sign + decimal.Parse(clock.Groups["s"].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), "SECOND"));
                }

                at += clock.Length;
                continue;
            }

            var m = Regex.Match(s[at..], @"\A(?<n>[-+]?\d+(?:\.\d+)?)\s*(?<u>[a-z]+)\s*");
            if (!m.Success)
            {
                return null;
            }

            var n = m.Groups["n"].Value.TrimStart('+');
            var unit = m.Groups["u"].Value switch
            {
                "y" or "yr" or "yrs" or "year" or "years" => "YEAR",
                "mon" or "mons" or "month" or "months" => "MONTH",
                "w" or "week" or "weeks" => "WEEK",
                "d" or "day" or "days" => "DAY",
                "h" or "hr" or "hrs" or "hour" or "hours" => "HOUR",
                "m" or "min" or "mins" or "minute" or "minutes" => "MINUTE",
                "s" or "sec" or "secs" or "second" or "seconds" => "SECOND",
                "ms" or "msec" or "msecs" or "millisecond" or "milliseconds" => "MILLISECOND",
                "us" or "usec" or "usecs" or "microsecond" or "microseconds" => "MICROSECOND",
                _ => null,
            };
            if (unit is null || (n.Contains('.', StringComparison.Ordinal) && unit != "SECOND"))
            {
                return null;
            }

            parts.Add(unit == "MILLISECOND"
                ? ((decimal.Parse(n, CultureInfo.InvariantCulture) * 1000).ToString(CultureInfo.InvariantCulture), "MICROSECOND")
                : (n, unit));
            at += m.Length;
        }

        // '02:30:00' has no seconds to add.
        var nonZero = parts.Where(p => decimal.Parse(p.Item1, CultureInfo.InvariantCulture) != 0).ToList();
        return parts.Count == 0 ? null : nonZero.Count > 0 ? nonZero : [parts[0]];
    }

    private string AtTimeZoneToMySql(PgAtTimeZone z)
    {
        if (StringArg(z.Zone)?.Equals("utc", StringComparison.OrdinalIgnoreCase) == true
            && Unparen(z.Operand) is PgCall { Function.Last: "now" or "current_timestamp" or "transaction_timestamp" or "statement_timestamp" })
        {
            return "UTC_TIMESTAMP(6)";
        }

        return Unsupported($"AT TIME ZONE not translated: use CONVERT_TZ() (it needs {My}'s time zone tables loaded)", Show(z.Operand) + " AT TIME ZONE " + Show(z.Zone));
    }

    private string MySqlExtract(string field, string source) => field switch
    {
        "year" or "month" or "day" or "hour" or "minute" or "quarter" => $"EXTRACT({field.ToUpperInvariant()} FROM {source})",

        // PostgreSQL's seconds carry the fraction.
        "second" => $"(EXTRACT(SECOND FROM {source}) + EXTRACT(MICROSECOND FROM {source}) / 1000000)",
        "milliseconds" => $"(EXTRACT(SECOND FROM {source}) * 1000 + EXTRACT(MICROSECOND FROM {source}) / 1000)",
        "microseconds" => $"(EXTRACT(SECOND FROM {source}) * 1000000 + EXTRACT(MICROSECOND FROM {source}))",
        "dow" => $"(DAYOFWEEK({source}) - 1)",
        "isodow" => $"(WEEKDAY({source}) + 1)",
        "doy" => $"DAYOFYEAR({source})",
        "week" => $"WEEK({source}, 3)",
        "isoyear" => $"(YEARWEEK({source}, 3) DIV 100)",
        "epoch" => $"UNIX_TIMESTAMP({source})",
        _ => Unsupported($"date field {field} not translated", $"extract({field} ...)"),
    };

    // ------------------------------------------------------------------ PostgreSQL → MySQL: casts and literals

    /// <summary>The MySQL CAST target for a PostgreSQL type (CAST takes only a few types), or null when there is none.</summary>
    private string? MySqlCastType(string pgType, out bool text, int depth = 0)
    {
        text = false;
        var t = pgType.Trim().ToLowerInvariant();
        if (t.EndsWith("[]", StringComparison.Ordinal) || depth > 5)
        {
            return null;
        }

        var paren = t.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? t : t[..paren]).Replace(" without time zone", string.Empty, StringComparison.Ordinal).Replace(" with time zone", string.Empty, StringComparison.Ordinal).Trim();
        var args = paren < 0 ? string.Empty : t[(paren + 1)..t.IndexOf(')', paren)].Replace(" ", string.Empty, StringComparison.Ordinal);
        var dot = name.LastIndexOf('.');
        var schema = dot < 0 ? ctx.Schema.DefaultSchema : name[..dot].Trim('"');
        var bare = (dot < 0 ? name : name[(dot + 1)..]).Trim('"');
        if (ctx.Enum(schema, bare) is not null || bare == "citext")
        {
            text = true;
            return "CHAR";
        }

        if (ctx.Domain(schema, bare) is { } domain)
        {
            return MySqlCastType(domain.BaseType, out text, depth + 1);
        }

        switch (bare)
        {
            case "smallint" or "integer" or "int" or "bigint" or "int2" or "int4" or "int8":
                return "SIGNED";
            case "numeric" or "decimal":
                return args.Length > 0 ? $"DECIMAL({args})" : "DECIMAL(65,30)";
            case "real" or "float4":
                return "FLOAT";
            case "double precision" or "float8" or "float":
                return "DOUBLE";
            case "text" or "varchar" or "character varying" or "char" or "character" or "bpchar" or "name":
                text = true;
                return args.Length > 0 && bare is not "text" ? $"CHAR({args})" : "CHAR";
            case "uuid":
                text = true;
                return "CHAR(36)";
            case "date":
                return "DATE";
            case "timestamp" or "timestamptz":
                return $"DATETIME({(args.Length > 0 ? args : "6")})";
            case "time" or "timetz":
                return $"TIME({(args.Length > 0 ? args : "6")})";
            case "json" or "jsonb":
                return "JSON";
            case "bytea":
                return "BINARY";
            default:
                return null;
        }
    }

    private string CastToMySql(PgCast c)
    {
        var type = c.Type.Trim().ToLowerInvariant();
        if (type is "regclass" or "regproc" or "regtype" or "oid" or "name" && c.Operand is PgLiteral)
        {
            return Expr(c.Operand);
        }

        if (type == "interval")
        {
            return Unsupported($"an interval value has no {My} counterpart (only date arithmetic is translated)", Show(c));
        }

        if (type is "boolean" or "bool")
        {
            return Unparen(c.Operand) switch
            {
                PgLiteral { Kind: LiteralKind.Number } n => n.Value.Trim('0', '.', '-').Length == 0 ? "FALSE" : "TRUE",
                var o when StringArg(o) is { } s => s.Trim().ToLowerInvariant() is "t" or "true" or "y" or "yes" or "on" or "1" ? "TRUE" : "FALSE",
                var o when IsBooleanColumn(o) => Expr(c.Operand),
                _ => $"({Expr(c.Operand)} <> 0)",
            };
        }

        var target = MySqlCastType(c.Type, out var text);
        if (text && StringArg(c.Operand) is { } literal)
        {
            return Lit(literal);
        }

        if (toMariaDb && target == "JSON")
        {
            // MariaDB's JSON is text: there is no CAST AS JSON, and none is needed.
            return Expr(c.Operand);
        }

        if (target is "SIGNED" or "FLOAT" or "DOUBLE" || target?.StartsWith("DECIMAL", StringComparison.Ordinal) == true)
        {
            if (Unparen(c.Operand) is PgLiteral { Kind: LiteralKind.Number } number)
            {
                return number.Value;
            }
        }

        return target is null
            ? Unsupported($"a cast to {c.Type} has no {My} counterpart", Show(c))
            : $"CAST({Expr(c.Operand)} AS {target})";
    }

    private string TypedLiteralToMySql(PgTypedLiteral t)
    {
        var type = t.Type.ToLowerInvariant();
        if (type == "interval")
        {
            return Unsupported($"an interval value has no {My} counterpart (only date arithmetic is translated)", $"interval '{t.Value}'");
        }

        if (type == "date")
        {
            return $"DATE {Lit(t.Value)}";
        }

        if (type.StartsWith("timestamp", StringComparison.Ordinal))
        {
            return $"TIMESTAMP {Lit(t.Value)}";
        }

        if (type.StartsWith("time", StringComparison.Ordinal))
        {
            return $"TIME {Lit(t.Value)}";
        }

        if (type == "bytea" && t.Value.StartsWith("\\x", StringComparison.OrdinalIgnoreCase))
        {
            return $"x'{t.Value[2..]}'";
        }

        return CastToMySql(new PgCast(new PgLiteral(LiteralKind.String, t.Value), t.Type));
    }

    // ------------------------------------------------------------------ PostgreSQL → MySQL: functions

    /// <summary>PostgreSQL functions in MySQL.</summary>
    private string ToMySql(string name, List<PgExpr> args, PgCall c)
    {
        string A(int i) => Expr(args[i]);
        string All() => string.Join(", ", args.Select(Expr));
        switch (name)
        {
            case "now" or "current_timestamp" or "transaction_timestamp" or "statement_timestamp" or "localtimestamp":
                return "CURRENT_TIMESTAMP";
            case "clock_timestamp":
                return "SYSDATE(6)";
            case "current_date":
                return "CURRENT_DATE";
            case "current_time" or "localtime":
                return "CURRENT_TIME";
            case "count" or "sum" or "avg" or "min" or "max":
                return Aggregate(name.ToUpperInvariant(), c, args);
            case "bool_and" or "every":
                return Aggregate("MIN", c, args);
            case "bool_or":
                return Aggregate("MAX", c, args);
            case "string_agg":
                return GroupConcat(c, args);
            case "array_agg" or "json_agg" or "jsonb_agg":
                return Aggregate("JSON_ARRAYAGG", c, args) + (name == "array_agg" ? Note("array_agg() became JSON_ARRAYAGG(): a JSON array") : string.Empty);
            case "json_object_agg" or "jsonb_object_agg":
                return Aggregate("JSON_OBJECTAGG", c, args);
            case "coalesce" or "nullif" or "lower" or "upper" or "replace" or "abs" or "round" or "floor" or "sign" or "sqrt" or "exp" or "ln" or "log10"
                or "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "atan2" or "pi" or "degrees" or "radians" or "mod" or "left" or "right" or "repeat"
                or "reverse" or "md5" or "ascii" or "row_number" or "rank" or "dense_rank" or "ntile" or "lag" or "lead" or "first_value" or "last_value"
                or "nth_value" or "percent_rank" or "cume_dist":
                return $"{name.ToUpperInvariant()}({All()})";
            case "greatest" or "least":
                return $"{name.ToUpperInvariant()}({All()})" + Note($"{My}'s {name.ToUpperInvariant()}() is NULL when any argument is NULL; PostgreSQL's {name}() skips NULLs");
            case "lpad" or "rpad":
                return $"{name.ToUpperInvariant()}({All()}{(args.Count == 2 ? ", ' '" : string.Empty)})";
            case "ceil" or "ceiling":
                return $"CEIL({A(0)})";
            case "power" or "pow":
                return $"POW({A(0)}, {A(1)})";
            case "log":
                return args.Count == 1 ? $"LOG10({A(0)})" : $"LOG({A(0)}, {A(1)})";
            case "trunc":
                return $"TRUNCATE({A(0)}, {(args.Count > 1 ? A(1) : "0")})";
            case "random":
                return "RAND()";
            case "length" or "char_length" or "character_length":
                return $"CHAR_LENGTH({A(0)})";
            case "octet_length":
                return $"LENGTH({A(0)})";
            case "substring" or "substr":
                return $"SUBSTRING({All()})";
            case "strpos":
                return $"LOCATE({A(1)}, {A(0)})";
            case "btrim" or "ltrim" or "rtrim":
                return TrimToMySql(name, args, c);
            case "concat":
                // PostgreSQL's concat() skips NULLs; MySQL's CONCAT() would be NULL.
                return $"CONCAT({string.Join(", ", args.Select(a => StringArg(a) is not null || !MaybeNull(a) ? Expr(a) : $"COALESCE({Expr(a)}, '')"))})";
            case "concat_ws":
                return $"CONCAT_WS({All()})";
            case "chr":
                return $"CHAR({A(0)} USING utf8mb4)";
            case "regexp_replace" when toMariaDb:
                return RegexpReplaceToMariaDb(args, c);
            case "regexp_replace" when RegexpReplaceToMySql(args) is { } replaced:
                return replaced;
            case "gen_random_uuid" or "uuid_generate_v4":
                return "UUID()";
            case "date_trunc" when StringArg(args[0]) is { } unit:
                return DateTruncToMySql(unit.ToLowerInvariant(), A(1), c);
            case "date_part" when StringArg(args[0]) is { } field:
                return MySqlExtract(field.ToLowerInvariant(), A(1));
            case "to_char" when args.Count == 2 && StringArg(args[1]) is { } fmt && ToMySqlDateFormat(fmt) is { } pattern:
                return $"DATE_FORMAT({A(0)}, {Lit(pattern)})";
            case "to_date" or "to_timestamp" when args.Count == 2 && StringArg(args[1]) is { } fmt && ToMySqlDateFormat(fmt) is { } pattern:
                return $"STR_TO_DATE({A(0)}, {Lit(pattern)})";
            case "to_timestamp" when args.Count == 1:
                return $"FROM_UNIXTIME({A(0)})";
            case "make_date":
                return $"STR_TO_DATE(CONCAT_WS('-', {All()}), '%Y-%c-%e')";
            case "nextval" or "currval" or "setval" when toMariaDb && args.Count > 0 && StringArg(args[0]) is { } sequence:
                return SequenceCallToMariaDb(name, sequence, args, c);
            case "nextval" or "currval" or "setval":
                return Unsupported($"{My} has no sequences: {name}() must become AUTO_INCREMENT or application logic", Show(c));
            case "current_user" or "session_user" or "user" or "current_role":
                return "CURRENT_USER()" + Note($"{My}'s CURRENT_USER() is 'user@host', not a role name");
            case "current_database" or "current_catalog" or "current_schema":
                return "DATABASE()";
            case "json_build_object" or "jsonb_build_object":
                return $"JSON_OBJECT({All()})";
            case "json_build_array" or "jsonb_build_array":
                return $"JSON_ARRAY({All()})";
            default:
                return $"{c.Function.Last}({All()})" + Note($"{c.Function.Last}() was kept as written; check that {My} has it");
        }
    }

    /// <summary>
    /// nextval / currval / setval on a MariaDB sequence: NEXTVAL(s), LASTVAL(s) (NULL rather than an error before the
    /// session's first NEXTVAL) and SETVAL(s, n[, is_used]). The sequence is named the way the conversion names tables.
    /// </summary>
    private string SequenceCallToMariaDb(string name, string sequence, List<PgExpr> args, PgCall c)
    {
        var parts = Regex.Matches(sequence, @"""((?:[^""]|"""")*)""|([^.""]+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal) : m.Groups[2].Value.Trim().ToLowerInvariant()).ToList();
        if (parts.Count is 0 or > 2)
        {
            return Unsupported($"sequence name {sequence} not understood", Show(c));
        }

        // The source's own sequence, whatever case the PostgreSQL text (or the chain's first step) gave its name.
        var named = new PortableName(parts.Count == 2 ? parts[0] : ctx.Schema.DefaultSchema, parts[^1]);
        named = ctx.Schema.Sequences.Select(s => s.Name).FirstOrDefault(s =>
            string.Equals(s.Schema, named.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Name, named.Name, StringComparison.OrdinalIgnoreCase)) ?? named;
        var target = ctx.Qualified(named);
        switch (name)
        {
            case "nextval":
                return $"NEXTVAL({target})";
            case "currval":
                return $"LASTVAL({target})";
            default:
                if (args.Count == 3 && Unparen(args[2]) is not PgLiteral { Kind: LiteralKind.True or LiteralKind.False })
                {
                    return Unsupported("setval() with a computed is_called flag not translated", Show(c));
                }

                // PostgreSQL's is_called is MariaDB's is_used: both default to true.
                var used = args.Count == 3 && Unparen(args[2]) is PgLiteral { Kind: LiteralKind.False } ? ", 0" : string.Empty;
                return $"SETVAL({target}, {Expr(args[1])}{used})";
        }
    }

    /// <summary>
    /// regexp_replace in MariaDB, whose REGEXP_REPLACE(subject, pattern, replacement) replaces every match and
    /// follows the collation: only PostgreSQL's 'g' forms translate, with PCRE's case flag in the pattern.
    /// </summary>
    private string RegexpReplaceToMariaDb(List<PgExpr> args, PgCall c)
    {
        var flags = args.Count == 4 ? StringArg(args[3]) : args.Count == 3 ? string.Empty : null;
        if (args.Count < 3 || StringArg(args[2]) is not { } replacement || replacement.Contains('\\', StringComparison.Ordinal) || flags is null)
        {
            return Unsupported("regexp_replace() with a computed or back-referencing replacement not translated", Show(c));
        }

        return flags switch
        {
            "g" => $"REGEXP_REPLACE({Expr(args[0])}, {PcrePattern(args[1], false)}, {Expr(args[2])})",
            "gi" or "ig" => $"REGEXP_REPLACE({Expr(args[0])}, {PcrePattern(args[1], true)}, {Expr(args[2])})",
            "" or "i" => Unsupported("MariaDB's REGEXP_REPLACE replaces every match; PostgreSQL's without 'g' only the first", Show(c)),
            _ => Unsupported($"regexp_replace() flags '{flags}' not translated", Show(c)),
        };
    }

    /// <summary>string_agg as GROUP_CONCAT, whose separator must be a literal.</summary>
    private string GroupConcat(PgCall c, List<PgExpr> args)
    {
        if (args.Count < 2 || StringArg(args[1]) is not { } separator)
        {
            return Unsupported($"string_agg() with a computed separator: {My}'s GROUP_CONCAT needs a literal one", Show(c));
        }

        var value = Expr(args[0]);
        if (c.Filter is not null)
        {
            value = $"CASE WHEN {Expr(c.Filter)} THEN {value} END";
        }

        var order = c.OrderBy.Count > 0 ? " " + OrderBy(c.OrderBy) : string.Empty;
        return $"GROUP_CONCAT({(c.Distinct ? "DISTINCT " : string.Empty)}{value}{order} SEPARATOR {Lit(separator)})"
            + Note($"GROUP_CONCAT() is cut off at group_concat_max_len ({(toMariaDb ? "1 MB" : "1024 bytes")} unless raised)");
    }

    /// <summary>
    /// trim functions. PostgreSQL removes any of a set of characters, MySQL's TRIM a whole string: the same only for a
    /// single character.
    /// </summary>
    private string TrimToMySql(string name, List<PgExpr> args, PgCall c)
    {
        if (args.Count == 1)
        {
            return $"{(name == "btrim" ? "TRIM" : name.ToUpperInvariant())}({Expr(args[0])})";
        }

        if (StringArg(args[1]) is { Length: 1 } ch)
        {
            var side = name switch { "ltrim" => "LEADING", "rtrim" => "TRAILING", _ => "BOTH" };
            return $"TRIM({side} {Lit(ch)} FROM {Expr(args[0])})";
        }

        return Unsupported($"trimming a set of characters has no {My} counterpart ({My}'s TRIM removes a whole string)", Show(c));
    }

    /// <summary>regexp_replace: PostgreSQL replaces the first match unless flagged 'g'; MySQL replaces all unless told the occurrence.</summary>
    private string? RegexpReplaceToMySql(List<PgExpr> args)
    {
        if (args.Count is < 3 or > 4 || StringArg(args[2]) is not { } replacement || replacement.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var flags = args.Count == 4 ? StringArg(args[3]) : string.Empty;
        return flags switch
        {
            "" => $"REGEXP_REPLACE({Expr(args[0])}, {Expr(args[1])}, {Expr(args[2])}, 1, 1, 'c')",
            "g" => $"REGEXP_REPLACE({Expr(args[0])}, {Expr(args[1])}, {Expr(args[2])}, 1, 0, 'c')",
            "i" => $"REGEXP_REPLACE({Expr(args[0])}, {Expr(args[1])}, {Expr(args[2])}, 1, 1, 'i')",
            "gi" or "ig" => $"REGEXP_REPLACE({Expr(args[0])}, {Expr(args[1])}, {Expr(args[2])}, 1, 0, 'i')",
            _ => null,
        };
    }

    private string DateTruncToMySql(string unit, string source, PgCall c) => unit switch
    {
        "year" => $"CAST(DATE_FORMAT({source}, '%Y-01-01') AS DATETIME)",
        "quarter" => $"CAST(MAKEDATE(YEAR({source}), 1) + INTERVAL (QUARTER({source}) - 1) QUARTER AS DATETIME)",
        "month" => $"CAST(DATE_FORMAT({source}, '%Y-%m-01') AS DATETIME)",
        "week" => $"CAST(DATE({source}) - INTERVAL WEEKDAY({source}) DAY AS DATETIME)",
        "day" => $"CAST(DATE({source}) AS DATETIME)",
        "hour" => $"CAST(DATE_FORMAT({source}, '%Y-%m-%d %H:00:00') AS DATETIME)",
        "minute" => $"CAST(DATE_FORMAT({source}, '%Y-%m-%d %H:%i:00') AS DATETIME)",
        "second" => $"CAST(DATE_FORMAT({source}, '%Y-%m-%d %H:%i:%s') AS DATETIME)",
        _ => Unsupported($"date_trunc('{unit}') not translated", Show(c)),
    };

    private static readonly (string Pg, string MySql)[] DateFormatTokens =
    [
        ("FMMonth", "%M"), ("FMDay", "%W"), ("FMDD", "%e"), ("FMMM", "%c"), ("FMHH24", "%k"), ("FMHH12", "%l"), ("YYYY", "%Y"), ("HH24", "%H"), ("HH12", "%h"),
        ("DDD", "%j"), ("Mon", "%b"), ("Dy", "%a"), ("YY", "%y"), ("MM", "%m"), ("DD", "%d"), ("HH", "%h"), ("MI", "%i"), ("SS", "%s"), ("US", "%f"),
        ("AM", "%p"), ("PM", "%p"),
    ];

    /// <summary>A to_char date format as a DATE_FORMAT one, or null when a part has no exact counterpart (or it formats numbers).</summary>
    private static string? ToMySqlDateFormat(string fmt)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < fmt.Length)
        {
            var token = DateFormatTokens.FirstOrDefault(t => string.CompareOrdinal(fmt, i, t.Pg, 0, t.Pg.Length) == 0);
            if (token.Pg is not null)
            {
                sb.Append(token.MySql);
                i += token.Pg.Length;
            }
            else if (fmt[i] == '"')
            {
                var close = fmt.IndexOf('"', i + 1);
                if (close < 0)
                {
                    return null;
                }

                sb.Append(fmt[(i + 1)..close].Replace("%", "%%", StringComparison.Ordinal));
                i = close + 1;
            }
            else if (char.IsLetterOrDigit(fmt[i]))
            {
                return null;
            }
            else
            {
                sb.Append(fmt[i] == '%' ? "%%" : fmt[i].ToString());
                i++;
            }
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ MySQL → PostgreSQL

    private string? BinaryFromMySql(PgBinary b)
    {
        switch (b.Op)
        {
            case "div":
                return IsInteger(b.Left) && IsInteger(b.Right) ? $"{Expr(b.Left)} / {Expr(b.Right)}" : $"div({Expr(b.Left)}, {Expr(b.Right)})";
            case "/" when IsInteger(b.Left) && IsInteger(b.Right):
                // MySQL's / always gives a decimal; PostgreSQL would divide integers as integers.
                return $"CAST({Expr(b.Left)} AS numeric) / {Expr(b.Right)}";
            case "=" or "<>" when IsBooleanColumn(b.Left) && BoolLiteral(b.Right) is { } r:
                return $"{Expr(b.Left)} {b.Op} {r}";
            case "=" or "<>" when IsBooleanColumn(b.Right) && BoolLiteral(b.Left) is { } l:
                return $"{l} {b.Op} {Expr(b.Right)}";
            case "->" or "->>" when StringArg(b.Right) is { } path:
                return JsonPathToPg(b.Left, path, b.Op == "->>", $"{Show(b.Left)} {b.Op} '{path}'");
            default:
                return null;
        }
    }

    /// <summary>MySQL's 1 / 0 for a boolean (tinyint(1)) column, as true / false.</summary>
    private static string? BoolLiteral(PgExpr e) => Unparen(e) is PgLiteral { Kind: LiteralKind.Number, Value: "1" or "0" } n ? (n.Value == "1" ? "true" : "false") : null;

    /// <summary>A MySQL JSON path ($.a.b[0], $."odd key") as PostgreSQL operators: doc -> 'a', or doc #> '{a,b,0}' for a longer path.</summary>
    private string JsonPathToPg(PgExpr doc, string path, bool text, string original)
    {
        var segments = MySqlJsonPath(path);
        if (segments is null)
        {
            return Unsupported("JSON path not translated (wildcards and ranges have no PostgreSQL operator)", original);
        }

        var target = Expr(doc);
        if (segments.Count == 0)
        {
            return text ? $"({target} #>> '{{}}')" : target;
        }

        if (segments.Count == 1)
        {
            var (key, index) = segments[0];
            return $"({target} {(text ? "->>" : "->")} {(index ? key : Lit(key))})";
        }

        var array = string.Join(',', segments.Select(s => !s.Index && Regex.IsMatch(s.Key, "[,{}\"\\\\\\s]|^$") ? "\"" + s.Key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : s.Key));
        return $"({target} {(text ? "#>>" : "#>")} {Lit("{" + array + "}")})";
    }

    private static List<(string Key, bool Index)>? MySqlJsonPath(string path)
    {
        var p = path.Trim();
        if (!p.StartsWith('$'))
        {
            return null;
        }

        var segments = new List<(string, bool)>();
        var i = 1;
        while (i < p.Length)
        {
            var m = Regex.Match(p[i..], @"\A(?:\.(?<key>[A-Za-z_$][A-Za-z0-9_$]*)|\.""(?<quoted>(?:[^""\\]|\\.)*)""|\[(?<index>\d+)\])");
            if (!m.Success)
            {
                return null;
            }

            segments.Add(m.Groups["index"].Success ? (m.Groups["index"].Value, true)
                : m.Groups["quoted"].Success ? (Regex.Unescape(m.Groups["quoted"].Value), false)
                : (m.Groups["key"].Value, false));
            i += m.Length;
        }

        return segments;
    }

    private string ExtractFromMySql(string field, PgExpr source)
    {
        var s = Expr(source);
        return field switch
        {
            "year" or "month" or "day" or "hour" or "minute" or "quarter" => $"EXTRACT({field} FROM {s})",
            "second" => $"floor(EXTRACT(second FROM {s}))",
            "microsecond" => $"(CAST(EXTRACT(microseconds FROM {s}) AS bigint) % 1000000)",
            "week" => $"EXTRACT(week FROM {s})" + Note($"{MySrc}'s EXTRACT(WEEK) counts weeks from Sunday (mode 0); PostgreSQL's are ISO weeks"),
            _ => Unsupported($"EXTRACT({field.ToUpperInvariant()}) not translated", $"EXTRACT({field} FROM {Show(source)})"),
        };
    }

    /// <summary>MySQL's CAST targets as PostgreSQL types; null when unknown.</summary>
    private static string? PgCastType(string mysqlType, out bool text)
    {
        text = false;
        var t = mysqlType.Trim().ToLowerInvariant();
        var paren = t.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? t : t[..paren]).Trim();
        var args = paren < 0 ? string.Empty : t[(paren + 1)..t.IndexOf(')', paren)].Replace(" ", string.Empty, StringComparison.Ordinal);
        switch (name)
        {
            case "signed" or "unsigned":
                return "bigint";
            case "integer" or "int":
                return "integer";
            case "char" or "nchar" or "varchar":
                text = true;
                return args.Length > 0 ? $"varchar({args})" : "text";
            case "binary":
                return "bytea";
            case "decimal" or "dec" or "numeric":
                return args.Length == 0 ? "numeric(10,0)" : args.Contains(',', StringComparison.Ordinal) ? $"numeric({args})" : $"numeric({args},0)";
            case "date":
                return "date";
            case "datetime":
                return args.Length > 0 ? $"timestamp({args})" : "timestamp(0)";
            case "time":
                return args.Length > 0 ? $"time({args})" : "time(0)";
            case "year":
                return "integer";
            case "json":
                return "jsonb";
            case "double" or "real":
                return "double precision";
            case "float":
                return "real";
            default:
                return null;
        }
    }

    private string CastFromMySql(PgCast c)
    {
        var target = PgCastType(c.Type, out var text);
        if (text && StringArg(c.Operand) is { } literal)
        {
            return Lit(literal);
        }

        return target is null
            ? $"CAST({Expr(c.Operand)} AS {c.Type})" + Note($"a cast to {c.Type} was kept as written; check the PostgreSQL type")
            : $"CAST({Expr(c.Operand)} AS {target})";
    }

    /// <summary>A MySQL condition as a PostgreSQL boolean: MySQL takes any number as a condition.</summary>
    private string Condition(PgExpr e) => IsBoolean(e) ? Expr(e) : $"({Expr(e)}) <> 0";

    private bool IsBoolean(PgExpr e) => Unparen(e) switch
    {
        PgBinary { Op: "=" or "<>" or "<" or ">" or "<=" or ">=" or "and" or "or" } => true,
        PgUnary { Op: "not" } or PgIs or PgIsDistinct or PgLike or PgBetween or PgInList or PgInQuery or PgExists or PgQuantified => true,
        PgLiteral { Kind: LiteralKind.True or LiteralKind.False } => true,
        var other => IsBooleanColumn(other),
    };

    /// <summary>A text operand of ||: string literals and text columns as they are, anything else cast to text.</summary>
    private string AsText(PgExpr e) =>
        StringArg(e) is not null || (Unparen(e) is PgName n && ctx.ColumnKindOf(n.Last) == ConversionContext.ColumnKind.Text) ? Expr(e) : $"CAST({Expr(e)} AS text)";

    /// <summary>MySQL functions in PostgreSQL.</summary>
    private string FromMySql(string name, List<PgExpr> args, PgCall c)
    {
        string A(int i) => Expr(args[i]);
        string All() => string.Join(", ", args.Select(Expr));
        switch (name)
        {
            case "ifnull":
                return $"coalesce({All()})";
            case "if" when args.Count == 3:
                return $"CASE WHEN {Condition(args[0])} THEN {A(1)} ELSE {A(2)} END";
            case "isnull" when args.Count == 1:
                return $"({A(0)} IS NULL)";
            case "now" or "localtimestamp" or "localtime":
                return "now()";
            case "current_timestamp":
                return "CURRENT_TIMESTAMP";
            case "sysdate":
                return "clock_timestamp()";
            case "utc_timestamp":
                return "(now() AT TIME ZONE 'utc')";
            case "curdate" or "current_date":
                return "CURRENT_DATE";
            case "utc_date":
                return "CAST((now() AT TIME ZONE 'utc') AS date)";
            case "curtime" or "current_time":
                return "LOCALTIME";
            case "uuid":
                return "gen_random_uuid()";
            case "concat":
                // MySQL's CONCAT() is NULL when any argument is, like ||.
                return args.Count == 1 ? AsText(args[0]) : "(" + string.Join(" || ", args.Select(AsText)) + ")";
            case "concat_ws":
                return $"concat_ws({All()})";
            case "length" or "octet_length":
                return $"octet_length({A(0)})";
            case "char_length" or "character_length":
                return $"char_length({A(0)})";
            case "substring" or "substr" or "mid":
                return args.Count > 1 && Unparen(args[1]) is PgLiteral { Kind: LiteralKind.Number } start && start.Value.StartsWith('-')
                    ? Unsupported($"SUBSTRING with a negative start counts from the end in {MySrc}; PostgreSQL has no such form", Show(c))
                    : $"substr({All()})";
            case "locate" when args.Count == 2:
                return $"strpos({A(1)}, {A(0)})";
            case "instr":
                return $"strpos({A(0)}, {A(1)})";
            case "strpos":
                return $"strpos({All()})";
            case "ucase":
                return $"upper({A(0)})";
            case "lcase":
                return $"lower({A(0)})";
            case "btrim" or "ltrim" or "rtrim":
                return args.Count == 1 || StringArg(args[1]) is { Length: 1 }
                    ? $"{name}({All()})"
                    : Unsupported("TRIM of a whole string has no PostgreSQL counterpart (PostgreSQL trims a set of characters)", Show(c));
            case "space":
                return $"repeat(' ', {A(0)})";
            case "coalesce" or "nullif" or "lower" or "upper" or "left" or "right" or "replace" or "repeat" or "reverse" or "lpad" or "rpad" or "ascii" or "md5"
                or "abs" or "round" or "floor" or "ceil" or "ceiling" or "sign" or "sqrt" or "exp" or "ln" or "log10" or "sin" or "cos" or "tan" or "asin"
                or "acos" or "atan" or "atan2" or "pi" or "degrees" or "radians" or "mod" or "row_number" or "rank" or "dense_rank" or "ntile" or "lag"
                or "lead" or "first_value" or "last_value" or "nth_value" or "percent_rank" or "cume_dist":
                return $"{name}({All()})";
            case "log":
                return args.Count == 1 ? $"ln({A(0)})" : $"log({A(0)}, {A(1)})";
            case "log2":
                return $"log(2, {A(0)})";
            case "pow" or "power":
                return $"power({A(0)}, {A(1)})";
            case "truncate":
                return $"trunc({A(0)}, {A(1)})";
            case "rand" when args.Count == 0:
                return "random()";
            case "greatest" or "least":
                return $"{name}({All()})" + Note($"PostgreSQL's {name}() skips NULLs; {MySrc}'s is NULL when any argument is NULL");
            case "count" when args.Count == 1 && !c.Distinct && Unparen(args[0]) is PgLiteral { Kind: not LiteralKind.Null }:
                // SHOW CREATE VIEW writes count(*) as count(0).
                return Aggregate("count", c with { StarArg = true }, []);
            case "count" or "min" or "max":
                return Aggregate(name, c, args);
            case "sum" or "avg":
                return Aggregate(name, c, [.. args.Select(a => IsBooleanColumn(a) ? new PgCast(a, "integer") : a)]);
            case "group_concat":
                return GroupConcatToPg(c, args);
            case "json_arrayagg":
                return Aggregate("json_agg", c, args);
            case "json_objectagg":
                return Aggregate("json_object_agg", c, args);
            case "json_object":
                return $"json_build_object({All()})";
            case "json_array":
                return $"json_build_array({All()})";
            case "json_extract" when args.Count == 2 && StringArg(args[1]) is { } path:
                return JsonPathToPg(args[0], path, false, Show(c));
            case "json_unquote" when Unparen(args[0]) is PgCall { Function.Last: var inner, Args: [var doc, var p] } && inner.Equals("json_extract", StringComparison.OrdinalIgnoreCase) && StringArg(p) is { } path:
                return JsonPathToPg(doc, path, true, Show(c));
            case "json_unquote":
                return $"({A(0)} #>> '{{}}')";
            case "json_value" when args.Count == 2 && StringArg(args[1]) is { } path:
                // MariaDB: the scalar at the path, as text.
                return JsonPathToPg(args[0], path, true, Show(c));
            case "nextval" or "lastval" when args.Count == 1 && Unparen(args[0]) is PgName sequence:
                // MariaDB sequences: NEXTVAL(s), and LASTVAL(s), the value this session last drew.
                return $"{(name == "nextval" ? "nextval" : "currval")}({Lit(ctx.Qualified(new PortableName(sequence.Parts.Count > 1 ? sequence.Parts[^2] : ctx.Schema.DefaultSchema, sequence.Last)))})";
            case "setval" when args.Count is >= 2 and <= 4 && Unparen(args[0]) is PgName sequence:
                return args.Count == 2
                    ? $"setval({Lit(ctx.Qualified(new PortableName(sequence.Parts.Count > 1 ? sequence.Parts[^2] : ctx.Schema.DefaultSchema, sequence.Last)))}, {A(1)})"
                    : Unsupported("SETVAL() with is_used or round arguments not translated", Show(c));
            case "date":
                return $"CAST({A(0)} AS date)";
            case "time":
                return $"CAST({A(0)} AS time)";
            case "year" or "month" or "day" or "dayofmonth" or "hour" or "minute" or "quarter":
                return $"EXTRACT({(name == "dayofmonth" ? "day" : name)} FROM {A(0)})";
            case "second":
                return $"floor(EXTRACT(second FROM {A(0)}))";
            case "dayofweek":
                return $"(EXTRACT(dow FROM {A(0)}) + 1)";
            case "weekday":
                return $"(EXTRACT(isodow FROM {A(0)}) - 1)";
            case "dayofyear":
                return $"EXTRACT(doy FROM {A(0)})";
            case "week" when args.Count == 2 && Unparen(args[1]) is PgLiteral { Value: "3" }:
                return $"EXTRACT(week FROM {A(0)})";
            case "date_add" or "date_sub" or "adddate" or "subdate" when args.Count == 2:
            {
                var op = name is "date_sub" or "subdate" ? "-" : "+";
                var amount = Unparen(args[1]) is PgTypedLiteral { Type: "interval" } or PgBinary { Right: PgTypedLiteral { Type: "interval" } }
                    ? A(1)
                    : $"({A(1)}) * interval '1 day'";
                return $"({A(0)} {op} {amount})";
            }

            case "datediff":
                return $"(CAST({A(0)} AS date) - CAST({A(1)} AS date))";
            case "timestampdiff" when args.Count == 3 && Unparen(args[0]) is PgName { Parts: [var unit] }:
                return TimestampDiff(unit.ToLowerInvariant(), A(1), A(2), c);
            case "unix_timestamp":
                return args.Count == 0 ? "CAST(extract(epoch FROM now()) AS bigint)" : $"extract(epoch FROM CAST({A(0)} AS timestamptz))";
            case "from_unixtime" when args.Count == 1:
                return $"CAST(to_timestamp({A(0)}) AS timestamp)";
            case "last_day":
                return $"CAST(date_trunc('month', {A(0)}) + interval '1 month - 1 day' AS date)";
            case "date_format" when args.Count == 2 && StringArg(args[1]) is { } fmt && FromMySqlDateFormat(fmt) is { } pattern:
                return $"to_char({A(0)}, {Lit(pattern)})";
            case "str_to_date" when args.Count == 2 && StringArg(args[1]) is { } fmt && FromMySqlDateFormat(fmt) is { } pattern:
                return Regex.IsMatch(fmt, "%[HhIiklrSsTfp]")
                    ? $"CAST(to_timestamp({A(0)}, {Lit(pattern)}) AS timestamp)"
                    : $"to_date({A(0)}, {Lit(pattern)})";
            case "last_insert_id":
                return "lastval()" + Note("LAST_INSERT_ID() became lastval(); prefer INSERT ... RETURNING");
            case "database" or "schema":
                return "current_schema()";
            case "user" or "current_user" or "session_user" or "system_user":
                return "current_user" + Note($"{MySrc}'s user functions return 'user@host'; PostgreSQL's current_user is a role name");
            case "regexp_like" when args.Count is 2 or 3:
            {
                var flags = args.Count == 3 ? StringArg(args[2]) : null;
                var insensitive = flags is null ? MySqlCaseInsensitive : flags.Contains('i', StringComparison.Ordinal) && !flags.Contains('c', StringComparison.Ordinal);
                return $"({A(0)} {(insensitive ? "~*" : "~")} {A(1)})";
            }

            case "regexp_replace" when args.Count == 3 && StringArg(args[2]) is { } replacement && !replacement.Contains('$', StringComparison.Ordinal):
                // MySQL replaces every match by default.
                return $"regexp_replace({All()}, '{(MySqlCaseInsensitive ? "gi" : "g")}')";
            default:
                return $"{name}({All()})" + Note($"{c.Function.Last}() was kept as written; check that PostgreSQL has it");
        }
    }

    /// <summary>GROUP_CONCAT([DISTINCT] a [, b ...] [ORDER BY ...] [SEPARATOR s]) as string_agg; the default separator is a comma.</summary>
    private string GroupConcatToPg(PgCall c, List<PgExpr> args)
    {
        var separator = c.ArgNames.Count == args.Count && args.Count > 0 && c.ArgNames[^1] == "separator" ? args[^1] : null;
        var values = separator is null ? args : args.Take(args.Count - 1).ToList();
        var value = values.Count == 1 ? $"CAST({Expr(values[0])} AS text)" : "(" + string.Join(" || ", values.Select(AsText)) + ")";
        var order = c.OrderBy.Count > 0 ? " " + OrderBy(c.OrderBy) : string.Empty;
        return $"string_agg({(c.Distinct ? "DISTINCT " : string.Empty)}{value}, {(separator is null ? "','" : Expr(separator))}{order})";
    }

    private string TimestampDiff(string unit, string from, string to, PgCall c)
    {
        var span = $"(CAST({to} AS timestamp) - CAST({from} AS timestamp))";
        var age = $"age(CAST({to} AS timestamp), CAST({from} AS timestamp))";
        string Seconds(string divisor) => $"CAST(trunc(extract(epoch FROM {span}){divisor}) AS bigint)";
        return unit switch
        {
            "microsecond" => Seconds(" * 1000000"),
            "second" => Seconds(string.Empty),
            "minute" => Seconds(" / 60"),
            "hour" => Seconds(" / 3600"),
            "day" => Seconds(" / 86400"),
            "week" => Seconds(" / 604800"),
            "month" => $"CAST(extract(year FROM {age}) * 12 + extract(month FROM {age}) AS bigint)",
            "quarter" => $"CAST(trunc((extract(year FROM {age}) * 12 + extract(month FROM {age})) / 3) AS bigint)",
            "year" => $"CAST(extract(year FROM {age}) AS bigint)",
            _ => Unsupported($"TIMESTAMPDIFF({unit.ToUpperInvariant()}) not translated", Show(c)),
        };
    }

    private static readonly Dictionary<char, string> MySqlFormatCodes = new()
    {
        ['Y'] = "YYYY", ['y'] = "YY", ['m'] = "MM", ['c'] = "FMMM", ['d'] = "DD", ['e'] = "FMDD", ['H'] = "HH24", ['k'] = "FMHH24", ['h'] = "HH12",
        ['I'] = "HH12", ['l'] = "FMHH12", ['i'] = "MI", ['s'] = "SS", ['S'] = "SS", ['f'] = "US", ['p'] = "AM", ['b'] = "Mon", ['M'] = "FMMonth",
        ['a'] = "Dy", ['W'] = "FMDay", ['j'] = "DDD", ['T'] = "HH24:MI:SS", ['r'] = "HH12:MI:SS AM", ['%'] = "%",
    };

    /// <summary>A DATE_FORMAT format as a to_char one (literal letters quoted), or null when a code has no counterpart.</summary>
    private static string? FromMySqlDateFormat(string fmt)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] == '%' && i + 1 < fmt.Length)
            {
                if (!MySqlFormatCodes.TryGetValue(fmt[++i], out var code))
                {
                    return null;
                }

                sb.Append(code);
            }
            else if (char.IsLetter(fmt[i]))
            {
                var start = i;
                while (i + 1 < fmt.Length && char.IsLetter(fmt[i + 1]))
                {
                    i++;
                }

                sb.Append('"').Append(fmt[start..(i + 1)]).Append('"');
            }
            else
            {
                sb.Append(fmt[i]);
            }
        }

        return sb.ToString();
    }
}
