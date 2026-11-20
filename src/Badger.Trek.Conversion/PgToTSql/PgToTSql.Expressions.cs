using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.PgToTSql;

/// <summary>
/// Translates PostgreSQL into T-SQL from <see cref="PgParser"/>'s syntax tree. T-SQL has no boolean values, so every
/// expression is written either as a value (<see cref="Value"/>: true becomes 1, a comparison becomes CASE WHEN ... THEN 1
/// ELSE 0 END) or as a condition (<see cref="Pred"/>: a boolean column becomes col = 1). Constructs without a faithful
/// counterpart are kept as REVIEW comments.
/// </summary>
internal sealed partial class PgToTSqlTranslator(ConversionContext ctx) : ITranslator
{
    private Scope scope = new("expression");

    private sealed class Scope(string owner)
    {
        public string Owner { get; } = owner;

        public List<string> Issues { get; } = [];

        /// <summary>PL/pgSQL variable or parameter (lowercase) → T-SQL @name and target type.</summary>
        public Dictionary<string, (string Name, string Type)> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Positional { get; } = [];

        /// <summary>Record variables (rec, NEW, OLD): field → variable.</summary>
        public Dictionary<string, Dictionary<string, (string Name, string Type)>> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Declarations to add at the top of the body.</summary>
        public List<string> Declarations { get; } = [];

        public bool UsesRowCount { get; set; }

        /// <summary>Trigger transition-table names (REFERENCING NEW TABLE AS x) → inserted / deleted.</summary>
        public Dictionary<string, string> TransitionTables { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Inside a trigger: TG_* values.</summary>
        public Dictionary<string, string> TriggerValues { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>NEW.* columns a BEFORE trigger assigns.</summary>
        public HashSet<string> AssignedNew { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Kind { get; set; } = "expression";

        public int Counter { get; set; }
    }

    private static readonly HashSet<string> ComparisonOps = new(StringComparer.Ordinal) { "=", "<>", "<", ">", "<=", ">=" };

    // ------------------------------------------------------------------ entry points

    public string Expression(string expression, ExpressionSite site)
    {
        scope = new Scope(site.Owner);
        try
        {
            var e = new PgParser(expression).ExpressionOnly();
            var result = Value(e);
            Flush();
            return result;
        }
        catch (PgParseException ex)
        {
            ctx.Issue(site.Owner, $"could not parse {expression}: {ex.Message}");
            return "NULL " + Review.Comment("could not translate", expression);
        }
    }

    public string Predicate(string predicate, ExpressionSite site)
    {
        scope = new Scope(site.Owner);
        try
        {
            var e = new PgParser(predicate).ExpressionOnly();
            var result = Pred(e);
            Flush();
            return result;
        }
        catch (PgParseException ex)
        {
            ctx.Issue(site.Owner, $"could not parse {predicate}: {ex.Message}");
            return "1 = 1 " + Review.Comment("could not translate", predicate);
        }
    }

    private void Flush()
    {
        foreach (var issue in scope.Issues.Distinct())
        {
            ctx.Issue(scope.Owner, issue);
        }
    }

    private string Unsupported(string why, string original, string placeholder = "NULL")
    {
        scope.Issues.Add($"{why}: {Collapse(original)}");
        return (placeholder.Length == 0 ? string.Empty : placeholder + " ") + Review.Comment(why, original);
    }

    private string Note(string message)
    {
        scope.Issues.Add(message);
        return " " + Review.Comment(message);
    }

    private static string Collapse(string text)
    {
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length > 120 ? collapsed[..117] + "..." : collapsed;
    }

    /// <summary>Approximate source text of an expression, for REVIEW comments.</summary>
    private static string Show(PgNode node) => node switch
    {
        PgName n => string.Join('.', n.Parts),
        PgLiteral l => l.Kind == LiteralKind.String ? $"'{l.Value}'" : l.Value,
        PgCall c => $"{string.Join('.', c.Function.Parts)}({string.Join(", ", c.Args.Select(Show))})",
        PgBinary b => $"{Show(b.Left)} {b.Op} {Show(b.Right)}",
        PgCast c => $"{Show(c.Operand)}::{c.Type}",
        PgArray a => $"ARRAY[{string.Join(", ", a.Elements.Select(Show))}]",
        PgTypedLiteral t => $"{t.Type} '{t.Value}'",
        PgStatement s => s.Source,
        _ => node.GetType().Name.Replace("Pg", string.Empty, StringComparison.Ordinal),
    };

    // ------------------------------------------------------------------ names

    private string Q(string name) => ConversionContext.TSqlQuote(name);

    private string Table(PgName name)
    {
        if (name.Parts.Count == 1 && scope.TransitionTables.TryGetValue(name.Last, out var transition))
        {
            return transition;
        }

        return name.Parts.Count >= 2
            ? $"{Q(ctx.Names.Schema(name.Parts[^2]))}.{Q(ctx.Names.Name(name.Last))}"
            : Q(ctx.Names.Name(name.Last));
    }

    private string Name(PgName n)
    {
        var first = n.Parts[0];
        if (n.Parts.Count == 1)
        {
            if (scope.Variables.TryGetValue(first, out var v))
            {
                return v.Name;
            }

            if (first.Equals("found", StringComparison.OrdinalIgnoreCase) && scope.Kind != "expression")
            {
                scope.UsesRowCount = true;
                return "CASE WHEN @trek_rowcount > 0 THEN 1 ELSE 0 END";
            }

            if (scope.TriggerValues.TryGetValue(first, out var tg))
            {
                return tg;
            }

            if (first.Equals("sqlerrm", StringComparison.OrdinalIgnoreCase))
            {
                return "ERROR_MESSAGE()";
            }

            if (first.Equals("sqlstate", StringComparison.OrdinalIgnoreCase))
            {
                return "CAST(ERROR_NUMBER() AS nvarchar(10))" + Note("SQLSTATE becomes ERROR_NUMBER(), a SQL Server error number");
            }

            return outputPrefix is null ? Q(ctx.Names.Name(first)) : $"{outputPrefix}.{Q(ctx.Names.Name(first))}";
        }

        if (n.Parts.Count == 2 && scope.Records.TryGetValue(first, out var record))
        {
            var field = n.Parts[1];
            if (!record.TryGetValue(field, out var variable))
            {
                variable = ($"@{SafeVar(first)}_{SafeVar(field)}", "sql_variant");
                record[field] = variable;
                scope.Declarations.Add($"DECLARE {variable.Name} sql_variant;");
                scope.Issues.Add($"the type of {first}.{field} is unknown; it was declared sql_variant");
            }

            return variable.Name;
        }

        if (n.Parts.Count == 3)
        {
            return $"{Q(ctx.Names.Schema(n.Parts[0]))}.{Q(ctx.Names.Name(n.Parts[1]))}.{Q(ctx.Names.Name(n.Parts[2]))}";
        }

        return string.Join('.', n.Parts.Select(p => scope.TransitionTables.TryGetValue(p, out var t) ? t : Q(ctx.Names.Name(p))));
    }

    /// <summary>A string literal, also when written 'x'::text (as pg_get_viewdef prints it).</summary>
    private static string? StringArg(PgExpr e) => e switch
    {
        PgLiteral { Kind: LiteralKind.String } s => s.Value,
        PgCast { Operand: PgLiteral { Kind: LiteralKind.String } s } => s.Value,
        PgCall { Function.Parts: ["("] } p => StringArg(p.Args[0]),
        _ => null,
    };

    private static PgExpr Unparen(PgExpr e) => e is PgCall { Function.Parts: ["("] } p ? Unparen(p.Args[0]) : e;

    private static string SafeVar(string name) => Regex.Replace(name, "[^A-Za-z0-9_]", "_");

    // ------------------------------------------------------------------ values

    private static bool IsPredicate(PgExpr e) => e switch
    {
        PgBinary b => b.Op is "and" or "or" || ComparisonOps.Contains(b.Op),
        PgUnary { Op: "not" } => true,
        PgIs or PgIsDistinct or PgBetween or PgInList or PgInQuery or PgLike or PgExists or PgQuantified => true,
        PgCall { Function.Parts: ["("] } c => IsPredicate(c.Args[0]),
        _ => false,
    };

    private string Value(PgExpr e)
    {
        if (IsPredicate(e))
        {
            return $"CASE WHEN {Pred(e)} THEN 1 ELSE 0 END";
        }

        switch (e)
        {
            case PgLiteral l:
                return l.Kind switch
                {
                    LiteralKind.String => ctx.Literal(l.Value),
                    LiteralKind.Null => "NULL",
                    LiteralKind.True => "1",
                    LiteralKind.False => "0",
                    LiteralKind.Default => "DEFAULT",
                    _ => l.Value,
                };
            case PgName n:
                return Name(n);
            case PgStar s:
                return s.Qualifier is null ? "*" : Name(s.Qualifier) + ".*";
            case PgParam p:
                return p.Index <= scope.Positional.Count ? scope.Positional[p.Index - 1] : $"@p{p.Index}";
            case PgCall c:
                return Call(c);
            case PgBinary b:
                return Binary(b);
            case PgUnary u:
                return u.Op == "~" ? "~" + Value(u.Operand) : u.Op + Value(u.Operand);
            case PgCast c:
                return Cast(c);
            case PgTypedLiteral t:
                return TypedLiteral(t);
            case PgCase c:
                return "CASE " + (c.Input is null ? string.Empty : Value(c.Input) + " ")
                    + string.Concat(c.Whens.Select(w => $"WHEN {(c.Input is null ? Pred(w.When) : Value(w.When))} THEN {Value(w.Then)} "))
                    + (c.Else is null ? string.Empty : $"ELSE {Value(c.Else)} ") + "END";
            case PgSubquery s:
                return "(" + Query(s.Query, QueryContext.Subquery) + ")";
            case PgExtract x:
                return Extract(x.Field, Value(x.Source));
            case PgAtTimeZone z when StringArg(z.Zone)?.Equals("utc", StringComparison.OrdinalIgnoreCase) == true:
                // now() AT TIME ZONE 'utc': the current UTC time; otherwise convert to UTC and drop the offset.
                return Unparen(z.Operand) is PgCall { Function.Last: "now" or "current_timestamp" or "transaction_timestamp" or "statement_timestamp" or "clock_timestamp" }
                    ? "SYSUTCDATETIME()"
                    : $"CAST(({Value(z.Operand)}) AT TIME ZONE 'UTC' AS datetime2)";
            case PgAtTimeZone z:
                return $"({Value(z.Operand)} AT TIME ZONE {Value(z.Zone)})" + Note("AT TIME ZONE: SQL Server takes Windows time zone names, not IANA names");
            case PgCollate c:
                return Value(c.Operand) + Note($"COLLATE {c.Collation} was dropped");
            case PgArray a:
                return Unsupported("arrays have no SQL Server counterpart", Show(a));
            case PgSubscript s:
                return Unsupported("array subscripts have no SQL Server counterpart", Show(s.Operand) + "[...]");
            case PgRow r:
                return Unsupported("row values are not translated", "ROW(...)");
            default:
                return Unsupported("expression not translated", Show(e));
        }
    }

    private string TextValue(PgExpr e)
    {
        var v = Value(e);
        return IsKnownText(e) ? v : $"CAST({v} AS nvarchar(max))";
    }

    private bool IsKnownText(PgExpr e) => e switch
    {
        PgLiteral { Kind: LiteralKind.String or LiteralKind.Null } => true,
        PgBinary { Op: "||" } => true,
        PgCall { Function.Parts: ["("] } p => IsKnownText(p.Args[0]),
        PgCast c => c.Type is "text" or "varchar" or "character varying" or "bpchar" || c.Type.StartsWith("character", StringComparison.Ordinal) || c.Type.StartsWith("varchar", StringComparison.Ordinal),
        PgCall c => c.Function.Last is "concat" or "concat_ws" or "lower" or "upper" or "trim" or "btrim" or "ltrim" or "rtrim" or "replace" or "substring" or "substr"
            or "left" or "right" or "lpad" or "rpad" or "repeat" or "format" or "to_char" or "initcap" or "md5" or "string_agg" or "coalesce",
        PgName n => n.Parts.Count == 1 && scope.Variables.TryGetValue(n.Last, out var v) ? v.Type.Contains("char", StringComparison.OrdinalIgnoreCase) : ctx.ColumnKindOf(n.Last) == ConversionContext.ColumnKind.Text,
        _ => false,
    };

    private string Binary(PgBinary b)
    {
        // Date arithmetic with intervals.
        if (b.Op is "+" or "-" && Interval(b.Right) is { } right)
        {
            return DateAdd(Value(b.Left), right, b.Op == "-");
        }

        if (b.Op == "+" && Interval(b.Left) is { } left)
        {
            return DateAdd(Value(b.Right), left, negate: false);
        }

        switch (b.Op)
        {
            case "||":
                return $"{TextValue(b.Left)} + {TextValue(b.Right)}";
            case "^":
                return $"POWER({Value(b.Left)}, {Value(b.Right)})";
            case "#":
                return $"{Value(b.Left)} ^ {Value(b.Right)}";
            case "->" or "->>" or "#>" or "#>>":
            {
                var path = JsonPath(b.Right, b.Op.StartsWith('#'));
                if (path is null)
                {
                    return Unsupported("JSON operator not translated", Show(b));
                }

                return b.Op.EndsWith(">>", StringComparison.Ordinal) ? $"JSON_VALUE({Value(b.Left)}, {ctx.Literal(path)})" : $"JSON_QUERY({Value(b.Left)}, {ctx.Literal(path)})"
                    + Note("-> returns JSON_QUERY: scalars come back NULL; use JSON_VALUE for them");
            }

            case "@>" or "<@" or "?" or "?|" or "?&" or "&&" or "@@" or "<<" or ">>":
                return Unsupported($"operator {b.Op} has no SQL Server counterpart", Show(b));
            case "-" when IsDateLike(b.Left) && IsDateLike(b.Right):
                return $"DATEDIFF(day, {Value(b.Right)}, {Value(b.Left)})";
            default:
                return $"{Value(b.Left)} {b.Op} {Value(b.Right)}";
        }
    }

    private static string? JsonPath(PgExpr key, bool pathArray)
    {
        if (key is PgLiteral { Kind: LiteralKind.Number } n)
        {
            return $"$[{n.Value}]";
        }

        if (key is PgLiteral { Kind: LiteralKind.String } s)
        {
            if (!pathArray)
            {
                return "$." + JsonKey(s.Value);
            }

            var parts = s.Value.Trim('{', '}').Split(',', StringSplitOptions.TrimEntries);
            return "$" + string.Concat(parts.Select(p => int.TryParse(p, out _) ? $"[{p}]" : "." + JsonKey(p)));
        }

        return null;
    }

    private static string JsonKey(string key) => Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$") ? key : $"\"{key.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static bool IsDateLike(PgExpr e) => e switch
    {
        PgCast c => c.Type is "date",
        PgTypedLiteral t => t.Type == "date",
        PgCall c => c.Function.Last is "current_date",
        _ => false,
    };

    /// <summary>An interval as (unit, amount expression) parts: interval '2 hours', '1 day'::interval, n * interval '1 day'.</summary>
    private List<(string Unit, string Amount)>? Interval(PgExpr e)
    {
        switch (e)
        {
            case PgTypedLiteral { Type: "interval" } t:
                return ParseInterval(t.Value);
            case PgCast { Type: "interval", Operand: PgLiteral { Kind: LiteralKind.String } s }:
                return ParseInterval(s.Value);
            case PgCall { Function.Parts: ["("] } p:
                return Interval(p.Args[0]);
            case PgBinary { Op: "*" } m:
            {
                var (interval, factor) = Interval(m.Right) is { Count: 1 } r ? (r, m.Left) : Interval(m.Left) is { Count: 1 } l ? (l, m.Right) : (null, null);
                if (interval is null)
                {
                    return null;
                }

                var amount = interval[0].Amount == "1" ? Value(factor!) : $"({Value(factor!)}) * {interval[0].Amount}";
                return [(interval[0].Unit, amount)];
            }

            default:
                return null;
        }
    }

    private static List<(string, string)>? ParseInterval(string text)
    {
        var parts = new List<(string, string)>();
        var t = text.Trim().ToLowerInvariant();
        var clock = Regex.Match(t, @"(?<sign>-?)(?<h>\d+):(?<m>\d{2})(?::(?<s>\d{2}(?:\.\d+)?))?$");
        if (clock.Success)
        {
            var sign = clock.Groups["sign"].Value;
            parts.Add(("hour", sign + clock.Groups["h"].Value));
            parts.Add(("minute", sign + clock.Groups["m"].Value));
            if (clock.Groups["s"].Success)
            {
                parts.Add(("second", sign + clock.Groups["s"].Value.Split('.')[0]));
            }

            t = t[..clock.Index];
        }

        foreach (Match m in Regex.Matches(t, @"(?<n>[-+]?\d+(?:\.\d+)?)\s*(?<u>[a-z]+)"))
        {
            var unit = m.Groups["u"].Value switch
            {
                "y" or "yr" or "yrs" or "year" or "years" => "year",
                "mon" or "mons" or "month" or "months" => "month",
                "w" or "week" or "weeks" => "week",
                "d" or "day" or "days" => "day",
                "h" or "hr" or "hrs" or "hour" or "hours" => "hour",
                "m" or "min" or "mins" or "minute" or "minutes" => "minute",
                "s" or "sec" or "secs" or "second" or "seconds" => "second",
                "ms" or "millisecond" or "milliseconds" => "millisecond",
                _ => null,
            };
            if (unit is null)
            {
                return null;
            }

            parts.Add((unit, m.Groups["n"].Value));
        }

        return parts.Count == 0 ? null : parts;
    }

    private static string DateAdd(string value, List<(string Unit, string Amount)> parts, bool negate)
    {
        var result = value;
        foreach (var (unit, amount) in parts)
        {
            var n = negate ? (amount.StartsWith('-') ? amount[1..] : "-" + (amount.Contains(' ', StringComparison.Ordinal) ? $"({amount})" : amount)) : amount;
            result = $"DATEADD({unit}, {n}, {result})";
        }

        return result;
    }

    private string Extract(string field, string source) => field switch
    {
        "epoch" => $"DATEDIFF_BIG(second, '1970-01-01', {source})",
        "dow" => $"(DATEPART(weekday, {source}) + @@DATEFIRST - 1) % 7",
        "isodow" => $"((DATEPART(weekday, {source}) + @@DATEFIRST - 2) % 7 + 1)",
        "doy" => $"DATEPART(dayofyear, {source})",
        "milliseconds" => $"DATEPART(millisecond, {source})",
        "microseconds" => $"DATEPART(microsecond, {source})",
        "year" or "quarter" or "month" or "week" or "day" or "hour" or "minute" or "second" => $"DATEPART({field}, {source})",
        "isoyear" => $"YEAR(DATEADD(day, 26 - DATEPART(iso_week, {source}), {source}))",
        _ => Unsupported($"date field {field} not translated", $"extract({field} from ...)"),
    };

    private string Cast(PgCast c)
    {
        var type = c.Type.ToLowerInvariant();
        if (type is "regclass" or "regproc" or "regprocedure" or "regtype" or "oid" or "name" && c.Operand is PgLiteral)
        {
            return Value(c.Operand);
        }

        if (type == "interval")
        {
            return Unsupported("an interval value has no SQL Server counterpart (only date arithmetic is translated)", Show(c));
        }

        if (c.Operand is PgLiteral { Kind: LiteralKind.String } s && (type is "text" or "character varying" or "varchar" or "bpchar" || type.StartsWith("character", StringComparison.Ordinal)))
        {
            return ctx.Literal(s.Value);
        }

        if (c.Operand is PgLiteral { Kind: LiteralKind.Null })
        {
            return "NULL";
        }

        // (0)::numeric, 100::bigint: a number literal needs no conversion.
        if (Unparen(c.Operand) is PgLiteral { Kind: LiteralKind.Number } number && type is "numeric" or "decimal" or "integer" or "int" or "int4" or "bigint" or "int8" or "smallint" or "real" or "double precision")
        {
            return number.Value;
        }

        var mapping = TypeMapper.PgToTSql(c.Type, ctx);
        if (mapping.IsText && StringArg(c.Operand) is { } text)
        {
            // 'new'::app.order_status, 'x'::citext: the literal is already text.
            return ctx.Literal(text);
        }

        if (type is "numeric" or "decimal")
        {
            // An expression cast to unbounded numeric (e.g. for exact division) needs a precision in T-SQL; no column is narrowed.
            return $"CAST({Value(c.Operand)} AS decimal(38,10))";
        }

        if (mapping.Note is not null)
        {
            scope.Issues.Add($"{c.Type}: {mapping.Note}");
        }

        return $"CAST({Value(c.Operand)} AS {mapping.Type})";
    }

    private string TypedLiteral(PgTypedLiteral t)
    {
        if (t.Type == "interval")
        {
            return Unsupported("an interval value has no SQL Server counterpart (only date arithmetic is translated)", $"interval '{t.Value}'");
        }

        var mapping = TypeMapper.PgToTSql(t.Type, ctx);
        return mapping.Type.StartsWith("nvarchar", StringComparison.Ordinal) ? ctx.Literal(t.Value) : $"CAST({ctx.Literal(t.Value)} AS {mapping.Type})";
    }

    // ------------------------------------------------------------------ conditions

    private string Pred(PgExpr e)
    {
        switch (e)
        {
            case PgCall { Function.Parts: ["("] } p:
                return "(" + Pred(p.Args[0]) + ")";
            case PgBinary { Op: "and" or "or" } b:
                return $"{Pred(b.Left)} {b.Op.ToUpperInvariant()} {Pred(b.Right)}";
            case PgBinary b when ComparisonOps.Contains(b.Op):
                return $"{Value(b.Left)} {b.Op} {Value(b.Right)}";
            case PgUnary { Op: "not" } u:
                return u.Operand is PgName or PgCall { Function.Parts: not ["("] } && !IsPredicate(u.Operand) ? $"{Value(u.Operand)} = 0" : $"NOT ({Pred(u.Operand)})";
            case PgIs i:
                return i.What switch
                {
                    "null" => $"{Value(i.Operand)} IS {(i.Not ? "NOT " : string.Empty)}NULL",
                    "true" => i.Not ? $"({Value(i.Operand)} = 0 OR {Value(i.Operand)} IS NULL)" : $"{Value(i.Operand)} = 1",
                    "false" => i.Not ? $"({Value(i.Operand)} = 1 OR {Value(i.Operand)} IS NULL)" : $"{Value(i.Operand)} = 0",
                    _ => $"{Value(i.Operand)} IS {(i.Not ? "NOT " : string.Empty)}NULL",
                };
            case PgIsDistinct d:
                return $"{Value(d.Left)} IS {(d.Not ? "NOT " : string.Empty)}DISTINCT FROM {Value(d.Right)}";
            case PgBetween b:
                return $"{Value(b.Operand)} {(b.Not ? "NOT " : string.Empty)}BETWEEN {Value(b.Low)} AND {Value(b.High)}";
            case PgInList i:
                return $"{Value(i.Operand)} {(i.Not ? "NOT " : string.Empty)}IN ({string.Join(", ", i.Values.Select(Value))})";
            case PgInQuery i:
                return $"{Value(i.Operand)} {(i.Not ? "NOT " : string.Empty)}IN ({Query(i.Query, QueryContext.Subquery)})";
            case PgExists x:
                return $"EXISTS ({Query(x.Query, QueryContext.Subquery)})";
            case PgLike l:
                return Like(l);
            case PgQuantified q:
                if (q.Query is not null)
                {
                    return $"{Value(q.Left)} {q.Op} {q.Quantifier.ToUpperInvariant()} ({Query(q.Query, QueryContext.Subquery)})";
                }

                if (q.Array is PgArray array && ((q.Op == "=" && q.Quantifier == "any") || (q.Op == "<>" && q.Quantifier == "all")))
                {
                    return $"{Value(q.Left)} {(q.Op == "=" ? string.Empty : "NOT ")}IN ({string.Join(", ", array.Elements.Select(Value))})";
                }

                return Unsupported("comparison with an array has no SQL Server counterpart", Show(q.Left) + $" {q.Op} {q.Quantifier}(...)", "1 = 1");
            case PgLiteral { Kind: LiteralKind.True }:
                return "1 = 1";
            case PgLiteral { Kind: LiteralKind.False }:
                return "1 = 0";
            default:
                return $"{Value(e)} = 1";
        }
    }

    private string Like(PgLike l)
    {
        var not = l.Not ? "NOT " : string.Empty;
        switch (l.Op)
        {
            case "like":
            case "ilike":
            {
                // [ is a wildcard in T-SQL LIKE patterns; PostgreSQL treats it literally.
                var pattern = StringArg(l.Pattern) is { } s ? ctx.Literal(s.Replace("[", "[[]", StringComparison.Ordinal)) : Value(l.Pattern);
                var escape = l.Escape is null ? string.Empty : $" ESCAPE {Value(l.Escape)}";
                var operand = l.Op == "ilike" ? $"LOWER({Value(l.Operand)})" : Value(l.Operand);
                var lowered = l.Op == "ilike" ? $"LOWER({pattern})" : pattern;
                return $"{operand} {not}LIKE {lowered}{escape}";
            }

            default:
                return Unsupported("regular-expression match (~, SIMILAR TO) has no T-SQL counterpart", $"{Show(l.Operand)} {l.Op} {Show(l.Pattern)}", "1 = 1");
        }
    }

    // ------------------------------------------------------------------ functions

    private HashSet<string>? userFunctions;
    private HashSet<string>? userProcedures;

    private HashSet<string> UserFunctions => userFunctions ??= ctx.Schema.Code.Where(c => c.Kind == PortableCodeKind.Function).Select(c => c.Name.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Routines this conversion writes as procedures (callable only with EXEC).</summary>
    internal HashSet<string> UserProcedures
    {
        get => userProcedures ??= ctx.Schema.Code.Where(c => c.Kind == PortableCodeKind.Procedure).Select(c => c.Name.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        set => userProcedures = value;
    }

    private string Call(PgCall c)
    {
        var name = c.Function.Last.ToLowerInvariant();
        var args = c.Args.ToList();
        string A(int i) => Value(args[i]);
        string All() => string.Join(", ", args.Select(Value));
        if (c.Function.Parts.Count == 1 && name == "(")
        {
            return "(" + Value(args[0]) + ")";
        }

        if (c.Function.Parts.Count > 1 || (UserFunctions.Contains(name) && !BuiltIns.Contains(name)))
        {
            if (UserProcedures.Contains(c.Function.Last))
            {
                scope.Issues.Add($"{c.Function.Last} became a procedure, so it cannot be called in an expression; call it with EXEC first");
            }

            var schema = c.Function.Parts.Count > 1 ? c.Function.Parts[^2] : ctx.Schema.DefaultSchema;
            return $"{Q(ctx.Names.Schema(schema))}.{Q(ctx.Names.Name(c.Function.Last))}({All()})";
        }

        if (c.Filter is not null)
        {
            var filter = Pred(c.Filter);
            var inner = c.StarArg || args.Count == 0 ? "1" : A(0);
            return $"{(name == "count" ? "COUNT" : name.ToUpperInvariant())}({(c.Distinct ? "DISTINCT " : string.Empty)}CASE WHEN {filter} THEN {inner} END)" + Over(c);
        }

        var distinct = c.Distinct ? "DISTINCT " : string.Empty;
        var result = name switch
        {
            "now" or "current_timestamp" or "transaction_timestamp" or "statement_timestamp" or "clock_timestamp" => "SYSDATETIMEOFFSET()",
            "localtimestamp" => "SYSDATETIME()",
            "current_date" => "CAST(SYSDATETIME() AS date)",
            "current_time" or "localtime" => "CAST(SYSDATETIME() AS time)",
            "current_user" or "session_user" or "user" or "current_role" => "SUSER_SNAME()",
            "current_database" or "current_catalog" => "DB_NAME()",
            "current_schema" => "SCHEMA_NAME()",
            "coalesce" or "nullif" or "greatest" or "least" or "lower" or "upper" or "replace" or "reverse" or "left" or "right" or "ascii" or "concat"
                or "concat_ws" or "abs" or "floor" or "sign" or "sqrt" or "exp" or "pi" or "power" or "sin" or "cos" or "tan" or "asin" or "acos" or "atan"
                or "degrees" or "radians" or "sum" or "avg" or "min" or "max" or "row_number" or "rank" or "dense_rank" or "ntile" or "lag" or "lead"
                or "first_value" or "last_value" or "percent_rank" or "cume_dist"
                => $"{name.ToUpperInvariant()}({distinct}{All()})",
            "count" => c.StarArg ? "COUNT(*)" : $"COUNT({distinct}{All()})",
            "length" or "char_length" or "character_length" => $"LEN({A(0)})",
            "octet_length" => $"DATALENGTH({A(0)})",
            "btrim" => args.Count == 1 ? $"TRIM({A(0)})" : $"TRIM({A(1)} FROM {A(0)})",
            "trim" => $"TRIM({A(0)})",
            "ltrim" or "rtrim" => args.Count == 1 ? $"{name.ToUpperInvariant()}({A(0)})" : $"{name.ToUpperInvariant()}({A(0)}, {A(1)})",
            "chr" => $"NCHAR({A(0)})",
            "substr" or "substring" => $"SUBSTRING({A(0)}, {A(1)}, {(args.Count > 2 ? A(2) : "2147483647")})",
            "strpos" => $"CHARINDEX({A(1)}, {A(0)})",
            "repeat" => $"REPLICATE({A(0)}, {A(1)})",
            "lpad" => $"RIGHT(REPLICATE({(args.Count > 2 ? A(2) : "N' '")}, {A(1)}) + {A(0)}, {A(1)})",
            "rpad" => $"LEFT({A(0)} + REPLICATE({(args.Count > 2 ? A(2) : "N' '")}, {A(1)}), {A(1)})",
            "md5" => $"LOWER(CONVERT(varchar(32), HASHBYTES('MD5', {A(0)}), 2))",
            "format" => Format(args),
            "to_char" => ToChar(args),
            "date_trunc" when StringArg(args[0]) is { } unit => $"DATETRUNC({DatePartName(unit)}, {A(1)})",
            "date_part" when StringArg(args[0]) is { } field => Extract(field.ToLowerInvariant(), A(1)),
            "to_timestamp" when args.Count == 1 => $"DATEADD(second, {A(0)}, CAST('1970-01-01' AS datetime2))",
            "make_date" => $"DATEFROMPARTS({All()})",
            "ceil" or "ceiling" => $"CEILING({A(0)})",
            "round" => args.Count == 1 ? $"ROUND({A(0)}, 0)" : $"ROUND({A(0)}, {A(1)})",
            "trunc" => args.Count == 1 ? $"ROUND({A(0)}, 0, 1)" : $"ROUND({A(0)}, {A(1)}, 1)",
            "ln" => $"LOG({A(0)})",
            "log" => args.Count == 1 ? $"LOG10({A(0)})" : $"LOG({A(1)}, {A(0)})",
            "log10" => $"LOG10({A(0)})",
            "mod" => $"({A(0)} % {A(1)})",
            "div" => $"({A(0)} / {A(1)})",
            "atan2" => $"ATN2({A(0)}, {A(1)})",
            "random" => "RAND()",
            "gen_random_uuid" or "uuid_generate_v4" => "NEWID()",
            "nextval" => NextVal(args),
            "string_agg" => $"STRING_AGG({A(0)}, {A(1)})" + (c.OrderBy.Count > 0 ? $" WITHIN GROUP ({OrderBy(c.OrderBy)})" : string.Empty),
            "bool_and" or "every" => $"MIN(CASE WHEN {Pred(args[0])} THEN 1 ELSE 0 END)",
            "bool_or" => $"MAX(CASE WHEN {Pred(args[0])} THEN 1 ELSE 0 END)",
            "stddev" or "stddev_samp" => $"STDEV({distinct}{A(0)})",
            "stddev_pop" => $"STDEVP({distinct}{A(0)})",
            "variance" or "var_samp" => $"VAR({distinct}{A(0)})",
            "var_pop" => $"VARP({distinct}{A(0)})",
            "isfinite" => "1",
            _ => Unsupported($"{c.Function.Last}() has no SQL Server counterpart", Show(c)),
        };

        return result + Over(c);
    }

    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase) { "now", "coalesce", "count", "sum", "min", "max", "avg", "lower", "upper", "length" };

    private static string DatePartName(string unit) => unit.ToLowerInvariant() switch
    {
        "microseconds" => "microsecond",
        "milliseconds" => "millisecond",
        "decade" or "century" or "millennium" => "year",
        var u => u,
    };

    private string NextVal(List<PgExpr> args)
    {
        var target = StringArg(args[0]);
        if (target is null)
        {
            return Unsupported("nextval with a computed sequence name", Show(args[0]));
        }

        var parts = target.Split('.').Select(p => p.Trim('"')).ToList();
        var schema = parts.Count > 1 ? parts[^2] : ctx.Schema.DefaultSchema;
        return $"NEXT VALUE FOR {Q(ctx.Names.Schema(schema))}.{Q(ctx.Names.Name(parts[^1]))}";
    }

    private string Format(List<PgExpr> args)
    {
        if (StringArg(args[0]) is not { } format)
        {
            return Unsupported("format() with a computed format string", "format(...)");
        }

        var fmt = new PgLiteral(LiteralKind.String, format);

        var note = fmt.Value.Contains("%I", StringComparison.Ordinal) || fmt.Value.Contains("%L", StringComparison.Ordinal)
            ? Note("format() %I/%L became %s: identifiers and literals are no longer quoted")
            : string.Empty;
        var pattern = Regex.Replace(fmt.Value, "%[ILs]", "%s");
        var rest = args.Skip(1).Select(a => $"CAST({Value(a)} AS nvarchar(4000))");
        return $"FORMATMESSAGE({string.Join(", ", new[] { ctx.Literal(pattern) }.Concat(rest))}){note}";
    }

    private static readonly (string Pg, string Net)[] DateTokens =
    [
        ("YYYY", "yyyy"), ("YY", "yy"), ("Month", "MMMM"), ("Mon", "MMM"), ("MM", "MM"), ("Day", "dddd"), ("Dy", "ddd"), ("DD", "dd"), ("HH24", "HH"),
        ("HH12", "hh"), ("HH", "hh"), ("MI", "mm"), ("SS", "ss"), ("MS", "fff"), ("US", "ffffff"), ("AM", "tt"), ("PM", "tt"),
    ];

    private string ToChar(List<PgExpr> args)
    {
        if (args.Count < 2 || StringArg(args[1]) is not { } format)
        {
            return Unsupported("to_char with a computed format", "to_char(...)");
        }

        var fmt = new PgLiteral(LiteralKind.String, format);

        var styles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["YYYY-MM-DD"] = "CONVERT(varchar(10), {0}, 23)",
            ["YYYYMMDD"] = "CONVERT(varchar(8), {0}, 112)",
            ["YYYY-MM-DD HH24:MI:SS"] = "CONVERT(varchar(19), {0}, 120)",
            ["HH24:MI:SS"] = "CONVERT(varchar(8), {0}, 108)",
        };
        if (styles.TryGetValue(fmt.Value, out var style))
        {
            return string.Format(CultureInfo.InvariantCulture, style, Value(args[0]));
        }

        if (Regex.IsMatch(fmt.Value, "[9D0G]") && !Regex.IsMatch(fmt.Value, "YY|MM|DD|HH|MI|SS"))
        {
            return $"FORMAT({Value(args[0])}, {ctx.Literal(fmt.Value.Replace('9', '#').Replace('D', '.').Replace('G', ','))})" + Note("to_char number format translated to a .NET format; check it");
        }

        var sb = new StringBuilder();
        var i = 0;
        var s = fmt.Value;
        while (i < s.Length)
        {
            var token = DateTokens.FirstOrDefault(t => string.CompareOrdinal(s, i, t.Pg, 0, t.Pg.Length) == 0);
            if (token.Pg is not null)
            {
                sb.Append(token.Net);
                i += token.Pg.Length;
            }
            else if (char.IsLetter(s[i]))
            {
                sb.Append('\\').Append(s[i]);
                i++;
            }
            else
            {
                sb.Append(s[i]);
                i++;
            }
        }

        return $"FORMAT({Value(args[0])}, {ctx.Literal(sb.ToString())})";
    }

    private string Over(PgCall c)
    {
        if (c.Over is not { } w)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (w.PartitionBy.Count > 0)
        {
            parts.Add("PARTITION BY " + string.Join(", ", w.PartitionBy.Select(Value)));
        }

        if (w.OrderBy.Count > 0)
        {
            parts.Add(OrderBy(w.OrderBy));
        }

        if (w.Frame is not null)
        {
            parts.Add(w.Frame);
        }

        return $" OVER ({string.Join(' ', parts)})";
    }

    private string OrderBy(IReadOnlyList<PgOrder> order) =>
        "ORDER BY " + string.Join(", ", order.Select(o =>
        {
            var v = Value(o.Expr);
            var nulls = o.Nulls switch
            {
                // T-SQL sorts NULLs first ascending and last descending.
                "last" when !o.Descending => $"CASE WHEN {v} IS NULL THEN 1 ELSE 0 END, ",
                "first" when o.Descending => $"CASE WHEN {v} IS NULL THEN 0 ELSE 1 END, ",
                _ => string.Empty,
            };
            return nulls + v + (o.Descending ? " DESC" : string.Empty);
        }));

    // ------------------------------------------------------------------ queries

    private enum QueryContext
    {
        /// <summary>A statement: ORDER BY is allowed.</summary>
        Statement,

        /// <summary>A view, subquery or derived table: ORDER BY only with TOP / OFFSET.</summary>
        Subquery,
    }

    private string Query(PgQuery q, QueryContext context, IReadOnlyList<string>? intoVariables = null)
    {
        var sb = new StringBuilder();
        if (q.With.Count > 0)
        {
            if (context == QueryContext.Subquery)
            {
                scope.Issues.Add("a WITH clause inside a subquery is not allowed in T-SQL; move it to the start of the statement");
            }

            sb.Append("WITH ").Append(string.Join(", ", q.With.Select(c =>
                $"{Q(ctx.Names.Name(c.Name))}{(c.Columns.Count == 0 ? string.Empty : "(" + string.Join(", ", c.Columns.Select(n => Q(ctx.Names.Name(n)))) + ")")} AS ({(c.Body is PgSelectStatement s ? Query(s.Query, QueryContext.Subquery) : Unsupported("data-modifying WITH is not translated", c.Body.Source, string.Empty))})")))
                .Append(' ');
        }

        var hasOrder = q.OrderBy.Count > 0;
        var top = q.Limit is not null && q.Offset is null && q.Body is PgSelectCore ? Value(q.Limit) : null;
        sb.Append(Body(q.Body, top, intoVariables));
        if (q.Offset is not null || (q.Limit is not null && top is null))
        {
            sb.Append(' ').Append(hasOrder ? OrderBy(q.OrderBy) : "ORDER BY (SELECT NULL)");
            sb.Append($" OFFSET {(q.Offset is null ? "0" : Value(q.Offset))} ROWS");
            if (q.Limit is not null)
            {
                sb.Append($" FETCH NEXT {Value(q.Limit)} ROWS ONLY");
            }
        }
        else if (hasOrder)
        {
            if (context == QueryContext.Statement || top is not null)
            {
                sb.Append(' ').Append(OrderBy(q.OrderBy));
            }
            else
            {
                sb.Append(Note("ORDER BY dropped: T-SQL views and subqueries cannot be ordered; order the outer query"));
            }
        }

        if (q.LockingClause)
        {
            sb.Append(Note("FOR UPDATE / FOR SHARE dropped; use WITH (UPDLOCK) table hints"));
        }

        return sb.ToString();
    }

    private string Body(PgQueryBody body, string? top, IReadOnlyList<string>? into) => body switch
    {
        PgSelectCore s => Select(s, top, into),
        PgSetOp op => $"{Body(op.Left, null, into)} {op.Op.ToUpperInvariant()}{(op.All ? op.Op == "union" ? " ALL" : Note($"{op.Op.ToUpperInvariant()} ALL is not supported; duplicates are removed") : string.Empty)} {Body(op.Right, null, null)}",
        PgNestedQuery n => "(" + Query(n.Query, QueryContext.Subquery) + ")",
        PgValuesBody v => $"SELECT * FROM (VALUES {string.Join(", ", v.Rows.Select(r => "(" + string.Join(", ", r.Select(Value)) + ")"))}) AS trek_values({string.Join(", ", Enumerable.Range(1, v.Rows[0].Count).Select(i => "column" + i))})",
        _ => Unsupported("query not translated", body.GetType().Name, "SELECT NULL"),
    };

    private string Select(PgSelectCore s, string? top, IReadOnlyList<string>? into)
    {
        var sb = new StringBuilder("SELECT ");
        if (s.Distinct)
        {
            sb.Append("DISTINCT ");
            if (s.DistinctOn is not null)
            {
                sb.Append(Note("DISTINCT ON is not supported: rewrite with ROW_NUMBER() OVER (PARTITION BY ...)").Trim()).Append(' ');
            }
        }

        if (top is not null)
        {
            sb.Append($"TOP ({top}) ");
        }

        var targets = s.Targets.Select((t, i) =>
        {
            var value = t.Expr is PgStar star ? (star.Qualifier is null ? "*" : Name(star.Qualifier) + ".*") : Value(t.Expr);
            var alias = t.Alias is null ? string.Empty : " AS " + Q(ctx.Names.Name(t.Alias));
            if (into is not null && i < into.Count)
            {
                return $"{into[i]} = {value}";
            }

            return value + alias;
        }).ToList();
        sb.Append(targets.Count == 0 ? "NULL AS trek_empty" : string.Join(", ", targets));
        if (s.From.Count > 0)
        {
            sb.Append(" FROM ").Append(string.Join(", ", s.From.Select(From)));
        }

        if (s.Where is not null)
        {
            sb.Append(" WHERE ").Append(Pred(s.Where));
        }

        if (s.GroupBy.Count > 0)
        {
            // GROUP BY 1, 2 refers to select-list positions, which T-SQL does not allow.
            sb.Append(" GROUP BY ").Append(string.Join(", ", s.GroupBy.Select(g => g is PgLiteral { Kind: LiteralKind.Number } n && int.TryParse(n.Value, out var i) && i >= 1 && i <= s.Targets.Count
                ? Value(s.Targets[i - 1].Expr)
                : Value(g))));
        }

        if (s.Having is not null)
        {
            sb.Append(" HAVING ").Append(Pred(s.Having));
        }

        return sb.ToString();
    }

    private string AliasText(string? alias, IReadOnlyList<string> columns) =>
        (alias is null ? string.Empty : " AS " + Q(ctx.Names.Name(alias)))
        + (columns.Count == 0 ? string.Empty : "(" + string.Join(", ", columns.Select(c => Q(ctx.Names.Name(c)))) + ")");

    private string From(PgFrom f)
    {
        switch (f)
        {
            case PgTableRef t:
                if (t.ColumnAliases.Count > 0)
                {
                    scope.Issues.Add($"column aliases on table {string.Join('.', t.Name.Parts)} are not supported; they were dropped");
                }

                return Table(t.Name) + (t.Alias is null ? string.Empty : " AS " + Q(ctx.Names.Name(t.Alias)));
            case PgSubqueryRef s:
                return $"({Query(s.Query, QueryContext.Subquery)}){AliasText(s.Alias ?? "trek_sub", s.ColumnAliases)}";
            case PgValuesRef v:
                return $"(VALUES {string.Join(", ", v.Rows.Select(r => "(" + string.Join(", ", r.Select(Value)) + ")"))}){AliasText(v.Alias ?? "trek_values", v.ColumnAliases.Count > 0 ? v.ColumnAliases : Enumerable.Range(1, v.Rows[0].Count).Select(i => "column" + i).ToList())}";
            case PgFunctionRef fn:
                return FunctionSource(fn);
            case PgJoin j:
                return Join(j);
            default:
                return Unsupported("FROM item not translated", f.GetType().Name, string.Empty);
        }
    }

    private string FunctionSource(PgFunctionRef fn)
    {
        var name = fn.Call.Function.Last.ToLowerInvariant();
        var alias = fn.Alias ?? name;
        var column = fn.ColumnAliases.Count > 0 ? fn.ColumnAliases[0] : alias;
        if (name == "generate_series" && fn.Call.Function.Parts.Count == 1)
        {
            var args = fn.Call.Args.Select(Value).ToList();
            return $"(SELECT value AS {Q(ctx.Names.Name(column))} FROM GENERATE_SERIES({string.Join(", ", args)})) AS {Q(ctx.Names.Name(alias))}";
        }

        if (name == "unnest" && fn.Call.Args is [PgCall { Function.Last: "string_to_array" } split])
        {
            return $"(SELECT value AS {Q(ctx.Names.Name(column))} FROM STRING_SPLIT({Value(split.Args[0])}, {Value(split.Args[1])})) AS {Q(ctx.Names.Name(alias))}";
        }

        if (fn.Call.Function.Parts.Count > 1 || UserFunctions.Contains(name))
        {
            return Call(fn.Call) + AliasText(alias, fn.ColumnAliases);
        }

        return Unsupported($"set-returning function {name}() is not translated", Show(fn.Call), "(SELECT NULL AS trek_unsupported)") + " AS " + Q(ctx.Names.Name(alias));
    }

    private string Join(PgJoin j)
    {
        if (j.Natural)
        {
            scope.Issues.Add("NATURAL JOIN: write the join condition out");
        }

        var lateral = j.Right is PgSubqueryRef { Lateral: true } or PgFunctionRef { Lateral: true };
        if (lateral)
        {
            var apply = j.Kind == "left" ? "OUTER APPLY" : "CROSS APPLY";
            if (j.On is not null && j.On is not PgLiteral { Kind: LiteralKind.True })
            {
                scope.Issues.Add("LATERAL join condition moved into APPLY: check it");
            }

            return $"{From(j.Left)} {apply} {From(j.Right)}";
        }

        var kind = j.Kind switch
        {
            "left" => "LEFT JOIN",
            "right" => "RIGHT JOIN",
            "full" => "FULL JOIN",
            "cross" => "CROSS JOIN",
            _ => "JOIN",
        };
        if (j.Kind == "cross")
        {
            return $"{From(j.Left)} CROSS JOIN {From(j.Right)}";
        }

        string on;
        if (j.On is not null)
        {
            on = Pred(j.On);
        }
        else if (j.Using.Count > 0)
        {
            var left = RefName(j.Left);
            var right = RefName(j.Right);
            on = left is null || right is null
                ? Unsupported("JOIN ... USING with a complex left side: write the ON condition out", "USING (" + string.Join(", ", j.Using) + ")", "1 = 1")
                : string.Join(" AND ", j.Using.Select(c => $"{left}.{Q(ctx.Names.Name(c))} = {right}.{Q(ctx.Names.Name(c))}"));
        }
        else
        {
            on = "1 = 1";
        }

        return $"{From(j.Left)} {kind} {From(j.Right)} ON {on}";
    }

    private string? RefName(PgFrom f) => f switch
    {
        PgTableRef t => t.Alias is null ? Table(t.Name) : Q(ctx.Names.Name(t.Alias)),
        PgSubqueryRef s when s.Alias is not null => Q(ctx.Names.Name(s.Alias)),
        PgJoin j => RefName(j.Right) is { } r && j.Using.Count > 0 ? r : null,
        _ => null,
    };
}
