using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Conversion.PgToTSql;

namespace Badger.Trek.Conversion.Sqlite;

/// <summary>
/// Writes <see cref="PgParser"/>'s syntax tree back out as SQL: as SQLite when the source is PostgreSQL, as PostgreSQL
/// when the source is SQLite. Both dialects share most syntax (|| concatenation, LIMIT/OFFSET, boolean expressions,
/// RETURNING, UPDATE ... FROM), so the work is in functions, dates, casts and the few operators one side lacks.
/// Untranslatable fragments become REVIEW comments.
/// </summary>
internal sealed partial class SqlPrinter(ConversionContext ctx, List<string> issues)
{
    private readonly bool toSqlite = ctx.Target == Dialect.Sqlite;

    /// <summary>Table names replaced while printing a trigger body (transition tables → a row of NEW/OLD).</summary>
    public Dictionary<string, string> TableReplacements { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names replaced while printing (PL/pgSQL variables, TG_OP): lowercase name → SQL.</summary>
    public Dictionary<string, string> NameReplacements { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The alias of an UPDATE or DELETE target, written as the table name: SQLite rejects an alias on the target
    /// table of a statement inside a trigger, the only place converted statements run.
    /// </summary>
    private readonly Dictionary<string, string> targetAliases = new(StringComparer.OrdinalIgnoreCase);

    private string WithoutTargetAlias(string? alias, PgName table, Func<string, string> print)
    {
        if (!toSqlite || alias is null)
        {
            return print(alias is null ? string.Empty : " AS " + ctx.Ident(alias));
        }

        targetAliases[alias] = Table(table);
        try
        {
            return print(string.Empty);
        }
        finally
        {
            targetAliases.Remove(alias);
        }
    }

    private string Unsupported(string why, string original, string placeholder = "NULL")
    {
        issues.Add($"{why}: {Collapse(original)}");
        return (placeholder.Length == 0 ? string.Empty : placeholder + " ") + Review.Comment(why, original);
    }

    private string Note(string message)
    {
        issues.Add(message);
        return " " + Review.Comment(message);
    }

    private static string Collapse(string text)
    {
        var c = Regex.Replace(text, @"\s+", " ").Trim();
        return c.Length > 120 ? c[..117] + "..." : c;
    }

    private static string Show(PgNode node) => node switch
    {
        PgName n => string.Join('.', n.Parts),
        PgLiteral l => l.Kind == LiteralKind.String ? $"'{l.Value}'" : l.Value,
        PgCall c => $"{string.Join('.', c.Function.Parts)}({string.Join(", ", c.Args.Select(Show))})",
        PgBinary b => $"{Show(b.Left)} {b.Op} {Show(b.Right)}",
        PgCast c => $"CAST({Show(c.Operand)} AS {c.Type})",
        PgStatement s => s.Source,
        _ => node.GetType().Name.Replace("Pg", string.Empty, StringComparison.Ordinal),
    };

    private string Lit(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static string? StringArg(PgExpr e) => e switch
    {
        PgLiteral { Kind: LiteralKind.String } s => s.Value,
        PgCast { Operand: PgLiteral { Kind: LiteralKind.String } s } => s.Value,
        PgCall { Function.Parts: ["("] } p => StringArg(p.Args[0]),
        _ => null,
    };

    private static PgExpr Unparen(PgExpr e) => e is PgCall { Function.Parts: ["("] } p ? Unparen(p.Args[0]) : e;

    // ------------------------------------------------------------------ names

    public string Table(PgName name)
    {
        if (name.Parts.Count == 1 && TableReplacements.TryGetValue(name.Last, out var replacement))
        {
            return replacement;
        }

        return name.Parts.Count >= 2 ? ctx.Qualified(new Core.Conversion.PortableName(name.Parts[^2], name.Last)) : ctx.Quote(ctx.TargetName(ctx.Schema.DefaultSchema, name.Last).Name);
    }

    private string Name(PgName n)
    {
        if (n.Parts.Count == 1 && NameReplacements.TryGetValue(n.Last, out var replaced))
        {
            return replaced;
        }

        if (n.Parts.Count == 2 && NameReplacements.TryGetValue(n.Parts[0] + "." + n.Parts[1], out var field))
        {
            return field;
        }

        if (n.Parts.Count == 2 && targetAliases.TryGetValue(n.Parts[0], out var target))
        {
            return $"{target}.{ctx.Ident(n.Parts[1])}";
        }

        if (n.Parts.Count == 2 && (n.Parts[0].Equals("new", StringComparison.OrdinalIgnoreCase) || n.Parts[0].Equals("old", StringComparison.OrdinalIgnoreCase)))
        {
            return n.Parts[0].ToUpperInvariant() + "." + ctx.Ident(n.Parts[1]);
        }

        if (n.Parts.Count >= 3)
        {
            var table = ctx.Qualified(new Core.Conversion.PortableName(n.Parts[^3], n.Parts[^2]));
            return $"{table}.{ctx.Ident(n.Last)}";
        }

        return string.Join('.', n.Parts.Select(p => TableReplacements.ContainsKey(p) ? p : ctx.Ident(p)));
    }

    // ------------------------------------------------------------------ expressions

    public string Expr(PgExpr e)
    {
        switch (e)
        {
            case PgLiteral l:
                return l.Kind switch
                {
                    LiteralKind.String => Lit(l.Value),
                    LiteralKind.Null => "NULL",
                    LiteralKind.True => "true",
                    LiteralKind.False => "false",
                    LiteralKind.Default => "DEFAULT",
                    _ => l.Value,
                };
            case PgName n:
                return Name(n);
            case PgStar s:
                return s.Qualifier is null ? "*" : Name(s.Qualifier) + ".*";
            case PgParam p:
                return NameReplacements.TryGetValue("$" + p.Index, out var arg) ? arg : toSqlite ? $"?{p.Index}" : $"${p.Index}";
            case PgCall c:
                return Call(c);
            case PgBinary b:
                return Binary(b);
            case PgUnary u:
                return u.Op == "not" ? "NOT " + Expr(u.Operand) : u.Op + Expr(u.Operand);
            case PgCast c:
                return Cast(c);
            case PgTypedLiteral t:
                return TypedLiteral(t);
            case PgCase c:
                return "CASE " + (c.Input is null ? string.Empty : Expr(c.Input) + " ")
                    + string.Concat(c.Whens.Select(w => $"WHEN {Expr(w.When)} THEN {Expr(w.Then)} "))
                    + (c.Else is null ? string.Empty : $"ELSE {Expr(c.Else)} ") + "END";
            case PgIs i:
                return $"{Expr(i.Operand)} IS {(i.Not ? "NOT " : string.Empty)}{i.What.ToUpperInvariant()}";
            case PgIsDistinct d:
                return toSqlite
                    ? $"{Expr(d.Left)} IS {(d.Not ? string.Empty : "NOT ")}{Expr(d.Right)}"
                    : $"{Expr(d.Left)} IS {(d.Not ? "NOT " : string.Empty)}DISTINCT FROM {Expr(d.Right)}";
            case PgBetween b:
                return $"{Expr(b.Operand)} {(b.Not ? "NOT " : string.Empty)}BETWEEN {Expr(b.Low)} AND {Expr(b.High)}";
            case PgInList i:
                return $"{Expr(i.Operand)} {(i.Not ? "NOT " : string.Empty)}IN ({string.Join(", ", i.Values.Select(Expr))})";
            case PgInQuery i:
                return $"{Expr(i.Operand)} {(i.Not ? "NOT " : string.Empty)}IN ({Query(i.Query)})";
            case PgLike l:
                return Like(l);
            case PgExists x:
                return $"EXISTS ({Query(x.Query)})";
            case PgSubquery s:
                return "(" + Query(s.Query) + ")";
            case PgQuantified q:
                return Quantified(q);
            case PgArray a when !toSqlite:
                return $"ARRAY[{string.Join(", ", a.Elements.Select(Expr))}]";
            case PgArray a:
                return $"json_array({string.Join(", ", a.Elements.Select(Expr))})" + Note("an array became a JSON array (json_array)");
            case PgSubscript s when !toSqlite:
                return $"{Expr(s.Operand)}[{Expr(s.Index)}]";
            case PgRow r when !toSqlite:
                return $"ROW({string.Join(", ", r.Fields.Select(Expr))})";
            case PgRow r:
                return $"({string.Join(", ", r.Fields.Select(Expr))})";
            case PgExtract x:
                return toSqlite ? Strftime(x.Field, Expr(x.Source)) : $"EXTRACT({x.Field} FROM {Expr(x.Source)})";
            case PgAtTimeZone z when !toSqlite:
                return $"({Expr(z.Operand)} AT TIME ZONE {Expr(z.Zone)})";
            case PgAtTimeZone z when StringArg(z.Zone)?.Equals("utc", StringComparison.OrdinalIgnoreCase) == true:
                // SQLite keeps times as UTC text already.
                return Unparen(z.Operand) is PgCall { Function.Last: "now" or "current_timestamp" or "transaction_timestamp" or "statement_timestamp" or "clock_timestamp" }
                    ? "CURRENT_TIMESTAMP"
                    : $"datetime({Expr(z.Operand)})";
            case PgCollate c:
                if (toSqlite && c.Collation.Trim('"').ToUpperInvariant() is "NOCASE" or "BINARY" or "RTRIM")
                {
                    return $"{Expr(c.Operand)} COLLATE {c.Collation.Trim('"').ToUpperInvariant()}";
                }

                if (!toSqlite && c.Collation.Trim('"').Equals("NOCASE", StringComparison.OrdinalIgnoreCase))
                {
                    return $"lower({Expr(c.Operand)})" + Note("COLLATE NOCASE became lower(): compare lower-cased text");
                }

                return Expr(c.Operand);
            default:
                return Unsupported("expression not translated", Show(e));
        }
    }

    private string Quantified(PgQuantified q)
    {
        if (!toSqlite)
        {
            return $"{Expr(q.Left)} {q.Op} {q.Quantifier.ToUpperInvariant()} ({(q.Query is not null ? Query(q.Query) : Expr(q.Array!))})";
        }

        if ((q.Op, q.Quantifier) is ("=", "any") or ("<>", "all"))
        {
            var not = q.Op == "<>" ? "NOT " : string.Empty;
            if (q.Query is not null)
            {
                return $"{Expr(q.Left)} {not}IN ({Query(q.Query)})";
            }

            if (q.Array is PgArray a)
            {
                return $"{Expr(q.Left)} {not}IN ({string.Join(", ", a.Elements.Select(Expr))})";
            }
        }

        return Unsupported("ANY/ALL comparison has no SQLite counterpart", Show(q.Left) + $" {q.Op} {q.Quantifier}(...)", "true");
    }

    private string Like(PgLike l)
    {
        var not = l.Not ? "NOT " : string.Empty;
        var escape = l.Escape is null ? string.Empty : $" ESCAPE {Expr(l.Escape)}";
        switch (l.Op)
        {
            case "like" when toSqlite:
                return $"{Expr(l.Operand)} {not}LIKE {Expr(l.Pattern)}{escape}" + (ctx.Schema.CaseInsensitive ? string.Empty : Note("SQLite's LIKE ignores the case of ASCII letters"));
            case "ilike" when toSqlite:
                return $"{Expr(l.Operand)} {not}LIKE {Expr(l.Pattern)}{escape}";
            case "like":
                // SQLite's LIKE ignores ASCII case.
                return $"{Expr(l.Operand)} {not}{(ctx.Source == Dialect.Sqlite ? "ILIKE" : "LIKE")} {Expr(l.Pattern)}{escape}";
            case "ilike":
                return $"{Expr(l.Operand)} {not}ILIKE {Expr(l.Pattern)}{escape}";
            case "glob" when StringArg(l.Pattern) is { } glob && !glob.Contains('[', StringComparison.Ordinal):
            {
                var like = glob.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace('*', '%').Replace('?', '_');
                return $"{Expr(l.Operand)} {not}LIKE {Lit(like)}" + (like.Contains('\\', StringComparison.Ordinal) ? " ESCAPE '\\'" : string.Empty);
            }

            case "similar" or "~" or "~*" when !toSqlite:
                return $"{Expr(l.Operand)} {(l.Op == "similar" ? not + "SIMILAR TO" : (l.Not ? "!" : string.Empty) + l.Op)} {Expr(l.Pattern)}";
            case "regexp" when !toSqlite:
                return $"{Expr(l.Operand)} {(l.Not ? "!" : string.Empty)}~ {Expr(l.Pattern)}";
            default:
                return Unsupported($"{l.Op.ToUpperInvariant()} has no counterpart", $"{Show(l.Operand)} {l.Op} {Show(l.Pattern)}", "true");
        }
    }

    private string Binary(PgBinary b)
    {
        if (toSqlite && b.Op is "+" or "-" && Interval(b.Right) is { } right)
        {
            return $"datetime({Expr(b.Left)}, {Modifiers(right, b.Op == "-")})";
        }

        if (toSqlite && b.Op == "+" && Interval(b.Left) is { } left)
        {
            return $"datetime({Expr(b.Right)}, {Modifiers(left, false)})";
        }

        if (toSqlite && b.Op == "-" && IsDateLike(b.Left) && IsDateLike(b.Right))
        {
            return $"CAST(julianday({Expr(b.Left)}) - julianday({Expr(b.Right)}) AS INTEGER)";
        }

        if (toSqlite && b.Op is "->" or "->>" or "#>" or "#>>")
        {
            return Json(b);
        }

        return b.Op switch
        {
            "and" or "or" => $"{Expr(b.Left)} {b.Op.ToUpperInvariant()} {Expr(b.Right)}",
            "^" when toSqlite => $"pow({Expr(b.Left)}, {Expr(b.Right)})",
            "#" or "@>" or "<@" or "?" or "?|" or "?&" or "&&" or "@@" when toSqlite => Unsupported($"operator {b.Op} has no SQLite counterpart", Show(b)),
            _ => $"{Expr(b.Left)} {b.Op} {Expr(b.Right)}",
        };
    }

    /// <summary>
    /// PostgreSQL's JSON operators as json_extract. SQLite's own -> and ->> need 3.38, and converted projects run on
    /// SQLite 3.26 and later (sqlite.minVersion's default). A chain (doc -> 'a' ->> 'b') becomes one path.
    /// </summary>
    private string Json(PgBinary b)
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

        string extract;
        if (ReferenceEquals(root, b))
        {
            if (b.Op is "#>" or "#>>")
            {
                return Unsupported("JSON path operator with a path that is not a literal", Show(b));
            }

            // A key that is only known at run time: taken as an object key.
            extract = $"json_extract({Expr(b.Left)}, '$.\"' || {Expr(b.Right)} || '\"')";
        }
        else
        {
            extract = $"json_extract({Expr(root)}, {Lit("$" + string.Concat(path))})";
        }

        return b.Op is "->>" or "#>>"
            ? extract
            : $"json_quote({extract})" + Note("-> became json_quote(json_extract(...)): a missing key gives 'null' instead of NULL");
    }

    /// <summary>The JSON path segments one -> / #> step adds, or null when its key or path is not a literal.</summary>
    private static List<string>? JsonSegments(PgBinary step)
    {
        if (step.Op is "#>" or "#>>")
        {
            return StringArg(step.Right) is { } p
                ? [.. p.Trim('{', '}').Split(',').Select(s => s.Trim()).Select(s => JsonSegment(s, int.TryParse(s, out var i) && i >= 0))]
                : null;
        }

        if (Unparen(step.Right) is PgLiteral { Kind: LiteralKind.Number } n)
        {
            return int.TryParse(n.Value, out var index) && index >= 0 ? [$"[{index}]"] : null;
        }

        return StringArg(step.Right) is { } key ? [JsonSegment(key, false)] : null;
    }

    private static string JsonSegment(string key, bool index) =>
        index ? $"[{key}]"
        : Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$") ? "." + key
        : ".\"" + key.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool IsDateLike(PgExpr e) => Unparen(e) switch
    {
        PgCast c => c.Type is "date",
        PgTypedLiteral t => t.Type == "date",
        PgCall c => c.Function.Last is "current_date",
        _ => false,
    };

    /// <summary>An interval as (unit, amount): interval '2 hours', '1 day'::interval, n * interval '1 day'.</summary>
    private List<(string Unit, string Amount)>? Interval(PgExpr e)
    {
        switch (Unparen(e))
        {
            case PgTypedLiteral { Type: "interval" } t:
                return ParseInterval(t.Value);
            case PgCast { Type: "interval", Operand: PgLiteral { Kind: LiteralKind.String } s }:
                return ParseInterval(s.Value);
            case PgBinary { Op: "*" } m:
            {
                var (interval, factor) = Interval(m.Right) is { Count: 1 } r ? (r, m.Left) : Interval(m.Left) is { Count: 1 } l ? (l, m.Right) : (null, null);
                return interval is null ? null : [(interval[0].Unit, interval[0].Amount == "1" ? Expr(factor!) : $"({Expr(factor!)}) * {interval[0].Amount}")];
            }

            default:
                return null;
        }
    }

    private static List<(string, string)>? ParseInterval(string text)
    {
        var parts = new List<(string, string)>();
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"(?<n>[-+]?\d+(?:\.\d+)?)\s*(?<u>[a-z]+)"))
        {
            var unit = m.Groups["u"].Value switch
            {
                "y" or "yr" or "yrs" or "year" or "years" => "years",
                "mon" or "mons" or "month" or "months" => "months",
                "w" or "week" or "weeks" => "week",
                "d" or "day" or "days" => "days",
                "h" or "hr" or "hrs" or "hour" or "hours" => "hours",
                "m" or "min" or "mins" or "minute" or "minutes" => "minutes",
                "s" or "sec" or "secs" or "second" or "seconds" => "seconds",
                _ => null,
            };
            if (unit is null)
            {
                return null;
            }

            parts.Add(unit == "week" ? ("days", (int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) * 7).ToString(CultureInfo.InvariantCulture)) : (unit, m.Groups["n"].Value));
        }

        return parts.Count == 0 ? null : parts;
    }

    /// <summary>SQLite date modifiers: '+30 days', or '+' || (n) || ' days' for computed amounts.</summary>
    private string Modifiers(List<(string Unit, string Amount)> parts, bool negate) =>
        string.Join(", ", parts.Select(p =>
        {
            var literal = Regex.IsMatch(p.Amount, @"^[-+]?\d+(\.\d+)?$");
            if (literal)
            {
                var n = decimal.Parse(p.Amount, CultureInfo.InvariantCulture) * (negate ? -1 : 1);
                return Lit($"{(n >= 0 ? "+" : string.Empty)}{n.ToString(CultureInfo.InvariantCulture)} {p.Unit}");
            }

            return $"{(negate ? "'-' || " : "'+' || ")}({p.Amount}) || ' {p.Unit}'";
        }));

    private string Strftime(string field, string source) => field switch
    {
        "year" => $"CAST(strftime('%Y', {source}) AS INTEGER)",
        "month" => $"CAST(strftime('%m', {source}) AS INTEGER)",
        "day" => $"CAST(strftime('%d', {source}) AS INTEGER)",
        "hour" => $"CAST(strftime('%H', {source}) AS INTEGER)",
        "minute" => $"CAST(strftime('%M', {source}) AS INTEGER)",
        "second" => $"CAST(strftime('%f', {source}) AS REAL)",
        "dow" => $"CAST(strftime('%w', {source}) AS INTEGER)",
        "doy" => $"CAST(strftime('%j', {source}) AS INTEGER)",
        "week" => $"CAST(strftime('%W', {source}) AS INTEGER)",
        "epoch" => $"CAST(strftime('%s', {source}) AS INTEGER)",
        "quarter" => $"((CAST(strftime('%m', {source}) AS INTEGER) + 2) / 3)",
        _ => Unsupported($"date field {field} not translated", $"extract({field} ...)"),
    };

    private string Cast(PgCast c)
    {
        var type = c.Type.ToLowerInvariant();
        if (type is "regclass" or "regproc" or "regtype" or "oid" or "name" && c.Operand is PgLiteral)
        {
            return Expr(c.Operand);
        }

        if (type == "interval")
        {
            return Unsupported("an interval value has no SQLite counterpart (only date arithmetic is translated)", Show(c));
        }

        var mapping = TypeMapper.Map(c.Type, ctx);
        if (mapping.IsText && StringArg(c.Operand) is { } text)
        {
            return Lit(text);
        }

        if (toSqlite && Unparen(c.Operand) is PgLiteral { Kind: LiteralKind.Number } number)
        {
            return number.Value;
        }

        return $"CAST({Expr(c.Operand)} AS {(toSqlite ? Affinity(mapping.Type) : mapping.Type)})";
    }

    /// <summary>The cast target in SQLite: one of its storage classes.</summary>
    private static string Affinity(string declared) => declared.ToUpperInvariant() switch
    {
        "INTEGER" or "BOOLEAN" => "INTEGER",
        "REAL" => "REAL",
        "BLOB" => "BLOB",
        var d when d.StartsWith("NUMERIC", StringComparison.Ordinal) => "NUMERIC",
        _ => "TEXT",
    };

    private string TypedLiteral(PgTypedLiteral t)
    {
        if (!toSqlite)
        {
            return $"{t.Type} {Lit(t.Value)}";
        }

        return t.Type == "interval" ? Unsupported("an interval value has no SQLite counterpart", $"interval '{t.Value}'") : Lit(t.Value);
    }

    // ------------------------------------------------------------------ functions

    private string Call(PgCall c)
    {
        if (c.Function.Parts is ["("])
        {
            return "(" + Expr(c.Args[0]) + ")";
        }

        var args = c.Args.ToList();
        var name = c.Function.Last.ToLowerInvariant();
        var userFunction = c.Function.Parts.Count > 1 || ctx.Schema.Code.Any(x => x.Kind == Core.Conversion.PortableCodeKind.Function && x.Name.Name.Equals(c.Function.Last, StringComparison.OrdinalIgnoreCase));
        if (userFunction && !(toSqlite && name == "raise"))
        {
            if (toSqlite)
            {
                return Inline(c) ?? Unsupported($"SQLite has no user functions: {string.Join('.', c.Function.Parts)}() must be inlined or computed by the application", Show(c));
            }

            var schema = c.Function.Parts.Count > 1 ? c.Function.Parts[^2] : ctx.Schema.DefaultSchema;
            return $"{ctx.Quote(ctx.Names.Schema(schema))}.{ctx.Ident(c.Function.Last)}({string.Join(", ", args.Select(Expr))})";
        }

        var result = toSqlite ? ToSqlite(name, args, c) : FromSqlite(name, args, c);
        return result + Over(c);
    }

    private readonly HashSet<string> inlining = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A call to a user function whose body is a single expression (RETURN expr; or SELECT expr), with the arguments
    /// substituted for the parameters: SQLite has no user functions, but such a function can be written in place.
    /// </summary>
    private string? Inline(PgCall c)
    {
        var function = ctx.Schema.Code.FirstOrDefault(x => x.Kind == Core.Conversion.PortableCodeKind.Function && x.Name.Name.Equals(c.Function.Last, StringComparison.OrdinalIgnoreCase));
        if (function is null || !inlining.Add(function.Name.Key))
        {
            return null;
        }

        try
        {
            var header = new PgParser(function.Definition).RoutineHeader();
            PgExpr? body = header.Language switch
            {
                "plpgsql" => new PgParser(header.Body).PlpgsqlBody() is { Declarations.Count: 0, Handlers.Count: 0, Body: [PgReturn { Value: { } value }] } ? value : null,
                "sql" => new PgParser(header.Body).SqlBody() is [PgSelectStatement { Query: { Body: PgSelectCore { From.Count: 0, Where: null, Targets: [var target] }, With.Count: 0 } }] ? target.Expr : null,
                _ => null,
            };
            if (body is null || header.Args.Count != c.Args.Count || header.Args.Any(a => a.Mode != "in"))
            {
                return null;
            }

            var saved = new Dictionary<string, string>(NameReplacements, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Args.Count; i++)
            {
                var value = "(" + Expr(c.Args[i]) + ")";
                if (header.Args[i].Name is { } name)
                {
                    NameReplacements[name] = value;
                }

                NameReplacements["$" + (i + 1)] = value;
            }

            var inlined = "(" + Expr(body) + ")";
            NameReplacements.Clear();
            foreach (var (k, v) in saved)
            {
                NameReplacements[k] = v;
            }

            issues.Add($"{string.Join('.', c.Function.Parts)}() was written in place (SQLite has no user functions); keep it in step with the original");
            return inlined;
        }
        catch (PgParseException)
        {
            return null;
        }
        finally
        {
            inlining.Remove(function.Name.Key);
        }
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
            parts.Add("PARTITION BY " + string.Join(", ", w.PartitionBy.Select(Expr)));
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

    private string Aggregate(string name, PgCall c, IReadOnlyList<PgExpr> args, string? extraArg = null)
    {
        var distinct = c.Distinct ? "DISTINCT " : string.Empty;
        List<string> values = c.StarArg ? ["*"] : [.. args.Select(Expr)];
        if (extraArg is not null)
        {
            values.Add(extraArg);
        }

        var order = c.OrderBy.Count > 0 ? " " + OrderBy(c.OrderBy) : string.Empty;
        var filter = c.Filter is null ? string.Empty : $" FILTER (WHERE {Expr(c.Filter)})";
        var note = string.Empty;
        if (toSqlite)
        {
            if (c.Filter is not null)
            {
                // FILTER (WHERE ...) needs SQLite 3.30. Aggregates skip NULLs, so a CASE without ELSE gives the same result.
                var condition = Expr(c.Filter);
                values[0] = c.StarArg ? $"CASE WHEN {condition} THEN 1 END" : $"CASE WHEN {condition} THEN {values[0]} END";
                filter = string.Empty;
            }

            if (order.Length > 0)
            {
                order = string.Empty;
                note = Note($"ORDER BY inside {name}() needs SQLite 3.44 and was left out: the values are in no particular order");
            }
        }

        return $"{name}({distinct}{string.Join(", ", values)}{order}){filter}{note}";
    }

    /// <summary>PostgreSQL functions in SQLite.</summary>
    private string ToSqlite(string name, List<PgExpr> args, PgCall c)
    {
        string A(int i) => Expr(args[i]);
        string All() => string.Join(", ", args.Select(Expr));
        return name switch
        {
            "now" or "current_timestamp" or "transaction_timestamp" or "statement_timestamp" or "clock_timestamp" => "CURRENT_TIMESTAMP",
            "localtimestamp" => "datetime('now', 'localtime')",
            "current_date" => "CURRENT_DATE",
            "current_time" or "localtime" => "CURRENT_TIME",
            "count" or "sum" or "avg" or "min" or "max" or "total" => Aggregate(name, c, args),
            "string_agg" => Aggregate("group_concat", c, args.Take(1).ToList(), args.Count > 1 ? A(1) : "','"),
            "array_agg" or "json_agg" or "jsonb_agg" => Aggregate("json_group_array", c, args),
            "coalesce" or "nullif" or "lower" or "upper" or "replace" or "abs" or "round" or "length" or "trim" or "ltrim" or "rtrim" or "substr" or "random"
                or "row_number" or "rank" or "dense_rank" or "ntile" or "lag" or "lead" or "first_value" or "last_value" or "percent_rank" or "cume_dist"
                or "sqrt" or "exp" or "ln" or "log10" or "floor" or "sign" or "sin" or "cos" or "tan" or "pi" or "degrees" or "radians" or "trunc"
                => $"{name}({All()})",
            "char_length" or "character_length" => $"length({A(0)})",
            "octet_length" => $"length(CAST({A(0)} AS BLOB))",
            "substring" => $"substr({All()})",
            "strpos" => $"instr({A(0)}, {A(1)})",
            "btrim" => $"trim({All()})",
            "left" => $"substr({A(0)}, 1, {A(1)})",
            "right" => $"substr({A(0)}, -({A(1)}))",
            "concat" => "(" + string.Join(" || ", args.Select(a => $"coalesce({Expr(a)}, '')")) + ")",
            "concat_ws" => "(" + string.Join(" || " + A(0) + " || ", args.Skip(1).Select(a => $"coalesce({Expr(a)}, '')")) + ")",
            "repeat" => $"replace(hex(zeroblob({A(1)})), '00', {A(0)})",
            "greatest" => $"max({All()})",
            "least" => $"min({All()})",
            "ceil" or "ceiling" => $"ceil({A(0)})",
            "power" => $"pow({A(0)}, {A(1)})",
            "log" => args.Count == 1 ? $"log10({A(0)})" : $"log({A(0)}, {A(1)})",
            "mod" => $"({A(0)} % {A(1)})",
            "div" => $"CAST({A(0)} / {A(1)} AS INTEGER)",
            "gen_random_uuid" or "uuid_generate_v4" => "lower(hex(randomblob(16)))" + Note("gen_random_uuid() became 32 random hex digits (no dashes, not RFC 4122 formatted)"),
            "date_trunc" when StringArg(args[0]) is { } unit => unit.ToLowerInvariant() switch
            {
                "year" => $"strftime('%Y-01-01 00:00:00', {A(1)})",
                "month" => $"strftime('%Y-%m-01 00:00:00', {A(1)})",
                "day" => $"strftime('%Y-%m-%d 00:00:00', {A(1)})",
                "hour" => $"strftime('%Y-%m-%d %H:00:00', {A(1)})",
                "minute" => $"strftime('%Y-%m-%d %H:%M:00', {A(1)})",
                _ => Unsupported($"date_trunc('{unit}') not translated", Show(c)),
            },
            "date_part" when StringArg(args[0]) is { } field => Strftime(field.ToLowerInvariant(), A(1)),
            "to_char" when args.Count == 2 && StringArg(args[1]) is { } fmt && ToStrftime(fmt) is { } pattern => $"strftime({Lit(pattern)}, {A(0)})",
            "to_timestamp" when args.Count == 1 => $"datetime({A(0)}, 'unixepoch')",
            "make_date" => $"printf('%04d-%02d-%02d', {All()})",
            "nextval" => "NULL" + Note("SQLite has no sequences: nextval() was dropped"),
            "md5" => Unsupported("SQLite has no md5()", Show(c)),
            "format" => $"printf({All()})" + Note("format() became printf(): %I and %L are not supported"),
            "json_build_object" or "jsonb_build_object" => $"json_object({All()})",
            "json_build_array" or "jsonb_build_array" => $"json_array({All()})",
            "to_json" or "to_jsonb" => $"json({A(0)})",
            "current_user" or "session_user" or "current_role" or "user" or "current_database" or "current_catalog" or "current_schema"
                => Unsupported($"{name} has no SQLite counterpart", Show(c), "''"),
            "raise" => $"RAISE({All()})",
            _ => Unsupported($"{c.Function.Last}() has no SQLite counterpart", Show(c)),
        };
    }

    private static readonly (string Pg, string Strftime)[] DateTokens =
    [
        ("YYYY", "%Y"), ("MM", "%m"), ("DD", "%d"), ("HH24", "%H"), ("MI", "%M"), ("SS", "%S"), ("MS", "%f"), ("DDD", "%j"),
    ];

    private static string? ToStrftime(string fmt)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < fmt.Length)
        {
            var token = DateTokens.OrderByDescending(t => t.Pg.Length).FirstOrDefault(t => string.CompareOrdinal(fmt, i, t.Pg, 0, t.Pg.Length) == 0);
            if (token.Pg is not null)
            {
                sb.Append(token.Strftime);
                i += token.Pg.Length;
            }
            else if (char.IsLetter(fmt[i]))
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

    /// <summary>SQLite functions in PostgreSQL.</summary>
    private string FromSqlite(string name, List<PgExpr> args, PgCall c)
    {
        string A(int i) => Expr(args[i]);
        string All() => string.Join(", ", args.Select(Expr));
        switch (name)
        {
            case "ifnull":
                return $"coalesce({All()})";
            case "instr":
                return $"strpos({A(0)}, {A(1)})";
            case "group_concat":
                return $"string_agg(CAST({A(0)} AS text), {(args.Count > 1 ? A(1) : "','")}{(c.OrderBy.Count > 0 ? " " + OrderBy(c.OrderBy) : string.Empty)})";
            case "total":
                return $"coalesce(sum({A(0)}), 0)";
            case "iif":
                return $"CASE WHEN {A(0)} THEN {A(1)} ELSE {A(2)} END";
            case "max" when args.Count > 1:
                return $"greatest({All()})";
            case "min" when args.Count > 1:
                return $"least({All()})";
            case "count" or "sum" or "avg" or "min" or "max":
                return Aggregate(name, c, args);
            case "current_timestamp":
                // SQLite's CURRENT_TIMESTAMP is UTC text without a zone.
                return "(now() AT TIME ZONE 'utc')";
            case "current_date" or "current_time":
                return $"CAST((now() AT TIME ZONE 'utc') AS {(name == "current_date" ? "date" : "time")})";
            case "datetime" or "date" or "time":
                return SqliteDate(name, args);
            case "julianday":
                return $"(extract(julian FROM {(args.Count == 0 || StringArg(args[0]) == "now" ? "now()" : $"CAST({A(0)} AS timestamp)")}))";
            case "unixepoch":
                return $"CAST(extract(epoch FROM {(args.Count == 0 || StringArg(args[0]) == "now" ? "now()" : $"CAST({A(0)} AS timestamptz)")}) AS bigint)";
            case "strftime" when StringArg(args[0]) is { } fmt:
                if (fmt == "%s")
                {
                    return $"CAST(extract(epoch FROM {Moment(args.Skip(1).FirstOrDefault())}) AS bigint)";
                }

                return $"to_char({Moment(args.Skip(1).FirstOrDefault())}, {Lit(FromStrftime(fmt))})";
            case "printf" or "format":
                return $"format({(StringArg(args[0]) is { } f ? Lit(Regex.Replace(f, "%[-+ 0#]*[0-9]*(?:\\.[0-9]+)?[dioxXucfeEgG]", "%s")) : A(0))}{string.Concat(args.Skip(1).Select(a => ", " + Expr(a)))})";
            case "random":
                return "floor(random() * 9223372036854775807)::bigint" + Note("random() in SQLite returns a random 64-bit integer; this is an approximation");
            case "typeof":
                return $"pg_typeof({A(0)})::text" + Note("typeof() returns PostgreSQL type names");
            case "hex":
                return $"upper(encode(CAST({A(0)} AS bytea), 'hex'))";
            case "quote":
                return $"quote_nullable({A(0)})";
            case "char":
                return string.Join(" || ", args.Select(a => $"chr({Expr(a)})"));
            case "unicode":
                return $"ascii({A(0)})";
            case "last_insert_rowid":
                return "lastval()" + Note("last_insert_rowid() became lastval(); prefer INSERT ... RETURNING");
            case "changes":
                return Unsupported("changes() has no PostgreSQL counterpart; use GET DIAGNOSTICS", Show(c));
            case "json_extract" when args.Count == 2:
                return $"jsonb_path_query_first(CAST({A(0)} AS jsonb), CAST({A(1)} AS jsonpath)) #>> '{{}}'";
            case "json_group_array":
                return Aggregate("json_agg", c, args);
            case "json_object":
                return $"json_build_object({All()})";
            case "json_array":
                return $"json_build_array({All()})";
            case "json":
                return $"CAST({A(0)} AS json)";
            case "raise":
                return Unsupported("RAISE() outside a trigger", Show(c));
            case "coalesce" or "nullif" or "lower" or "upper" or "replace" or "abs" or "round" or "length" or "trim" or "ltrim" or "rtrim" or "substr" or "substring"
                or "row_number" or "rank" or "dense_rank" or "ntile" or "lag" or "lead" or "first_value" or "last_value" or "percent_rank" or "cume_dist"
                or "sqrt" or "exp" or "ln" or "log10" or "floor" or "ceil" or "ceiling" or "sign" or "sin" or "cos" or "tan" or "pi" or "degrees" or "radians"
                or "trunc" or "pow" or "power" or "log" or "mod":
                return $"{(name == "pow" ? "power" : name)}({All()})";
            default:
                return $"{name}({All()})" + Note($"{c.Function.Last}() was kept as written; check that PostgreSQL has it");
        }
    }

    private string Moment(PgExpr? e) => e is null || StringArg(e) == "now" ? "now()" : $"CAST({Expr(e)} AS timestamp)";

    private static string FromStrftime(string fmt) =>
        fmt.Replace("%Y", "YYYY", StringComparison.Ordinal).Replace("%m", "MM", StringComparison.Ordinal).Replace("%d", "DD", StringComparison.Ordinal)
            .Replace("%H", "HH24", StringComparison.Ordinal).Replace("%M", "MI", StringComparison.Ordinal).Replace("%S", "SS", StringComparison.Ordinal)
            .Replace("%f", "SS.MS", StringComparison.Ordinal).Replace("%j", "DDD", StringComparison.Ordinal).Replace("%w", "D", StringComparison.Ordinal)
            .Replace("%W", "WW", StringComparison.Ordinal).Replace("%%", "%", StringComparison.Ordinal);

    /// <summary>SQLite's datetime(), date(), time() with modifiers ('now', '+1 day', 'start of month', 'localtime').</summary>
    private string SqliteDate(string function, List<PgExpr> args)
    {
        var value = args.Count == 0 || StringArg(args[0]) == "now" ? "(now() AT TIME ZONE 'utc')" : $"CAST({Expr(args[0])} AS timestamp)";
        foreach (var modifier in args.Skip(1))
        {
            var m = StringArg(modifier)?.Trim().ToLowerInvariant();
            if (m is null)
            {
                value += Note("a computed date modifier was dropped");
                continue;
            }

            var amount = Regex.Match(m, @"^(?<n>[-+]?\d+(?:\.\d+)?)\s+(?<u>years?|months?|days?|hours?|minutes?|seconds?)$");
            if (amount.Success)
            {
                value = $"({value} + interval {Lit($"{amount.Groups["n"].Value} {amount.Groups["u"].Value}")})";
            }
            else if (m.StartsWith("start of ", StringComparison.Ordinal))
            {
                value = $"date_trunc({Lit(m[9..])}, {value})";
            }
            else if (m is "localtime")
            {
                value = $"(({value}) AT TIME ZONE 'utc' AT TIME ZONE current_setting('TimeZone'))";
            }
            else if (m is "utc" or "unixepoch" && args.Count == 2 && m == "unixepoch")
            {
                value = $"(to_timestamp({Expr(args[0])}) AT TIME ZONE 'utc')";
            }
            else
            {
                value += Note($"date modifier '{m}' was dropped");
            }
        }

        return function switch
        {
            "date" => $"CAST({value} AS date)",
            "time" => $"CAST({value} AS time)",
            _ => value,
        };
    }

    // ------------------------------------------------------------------ queries

    public string OrderBy(IReadOnlyList<PgOrder> order) => "ORDER BY " + string.Join(", ", order.Select(OrderTerm));

    private string OrderTerm(PgOrder o)
    {
        var term = Expr(o.Expr) + (o.Descending ? " DESC" : string.Empty);
        if (o.Nulls is null)
        {
            return term;
        }

        var nullsLast = o.Nulls.Equals("last", StringComparison.OrdinalIgnoreCase);
        if (!toSqlite)
        {
            return term + (nullsLast ? " NULLS LAST" : " NULLS FIRST");
        }

        // NULLS FIRST / LAST needs SQLite 3.30. SQLite sorts NULLs first ascending and last descending; otherwise sort on
        // IS NULL first.
        return nullsLast == o.Descending ? term : $"{Expr(o.Expr)} IS {(nullsLast ? string.Empty : "NOT ")}NULL, {term}";
    }

    public string Query(PgQuery q)
    {
        var sb = new StringBuilder();
        if (q.With.Count > 0)
        {
            sb.Append("WITH ").Append(q.Recursive ? "RECURSIVE " : string.Empty).Append(string.Join(", ", q.With.Select(c =>
                $"{ctx.Ident(c.Name)}{(c.Columns.Count == 0 ? string.Empty : "(" + string.Join(", ", c.Columns.Select(ctx.Ident)) + ")")} AS ({(c.Body is PgSelectStatement s ? Query(s.Query) : Statement(c.Body).TrimEnd(';'))})")))
                .Append(' ');
        }

        sb.Append(Body(q.Body));
        if (q.OrderBy.Count > 0)
        {
            sb.Append(' ').Append(OrderBy(q.OrderBy));
        }

        if (q.Limit is not null)
        {
            sb.Append(" LIMIT ").Append(Expr(q.Limit));
        }

        if (q.Offset is not null)
        {
            sb.Append(q.Limit is null && toSqlite ? " LIMIT -1" : string.Empty).Append(" OFFSET ").Append(Expr(q.Offset));
        }

        if (q.LockingClause)
        {
            sb.Append(Note("FOR UPDATE / FOR SHARE dropped"));
        }

        return sb.ToString();
    }

    private string Body(PgQueryBody body) => body switch
    {
        PgSelectCore s => Select(s),
        PgSetOp op => $"{Body(op.Left)} {op.Op.ToUpperInvariant()}{(op.All ? " ALL" : string.Empty)} {Body(op.Right)}",
        PgNestedQuery n => toSqlite ? "SELECT * FROM (" + Query(n.Query) + ")" : "(" + Query(n.Query) + ")",
        PgValuesBody v => "VALUES " + string.Join(", ", v.Rows.Select(r => "(" + string.Join(", ", r.Select(Expr)) + ")")),
        _ => Unsupported("query not translated", body.GetType().Name, "SELECT NULL"),
    };

    private string Select(PgSelectCore s)
    {
        var sb = new StringBuilder("SELECT ");
        if (s.Distinct)
        {
            if (s.DistinctOn is not null && toSqlite)
            {
                sb.Append("DISTINCT ").Append(Note("DISTINCT ON is not supported: rewrite with ROW_NUMBER() OVER (PARTITION BY ...)").Trim()).Append(' ');
            }
            else
            {
                sb.Append(s.DistinctOn is null ? "DISTINCT " : $"DISTINCT ON ({string.Join(", ", s.DistinctOn.Select(Expr))}) ");
            }
        }

        sb.Append(s.Targets.Count == 0 ? "NULL" : string.Join(", ", s.Targets.Select(t => Expr(t.Expr) + (t.Alias is null ? string.Empty : " AS " + ctx.Ident(t.Alias)))));
        if (s.From.Count > 0)
        {
            sb.Append(" FROM ").Append(string.Join(", ", s.From.Select(From)));
        }

        if (s.Where is not null)
        {
            sb.Append(" WHERE ").Append(Expr(s.Where));
        }

        if (s.GroupBy.Count > 0)
        {
            sb.Append(" GROUP BY ").Append(string.Join(", ", s.GroupBy.Select(Expr)));
        }

        if (s.Having is not null)
        {
            sb.Append(" HAVING ").Append(Expr(s.Having));
        }

        return sb.ToString();
    }

    private string Alias(string? alias, IReadOnlyList<string> columns) =>
        (alias is null ? string.Empty : " AS " + ctx.Ident(alias)) + (columns.Count == 0 ? string.Empty : "(" + string.Join(", ", columns.Select(ctx.Ident)) + ")");

    public string From(PgFrom f)
    {
        switch (f)
        {
            case PgTableRef t:
                if (t.ColumnAliases.Count > 0 && toSqlite)
                {
                    issues.Add($"column aliases on table {string.Join('.', t.Name.Parts)} are not supported in SQLite; they were dropped");
                }

                var table = Table(t.Name);
                var alias = t.Alias ?? (t.Name.Parts.Count == 1 && TableReplacements.ContainsKey(t.Name.Last) ? t.Name.Last : null);
                return table + (alias is null ? string.Empty : " AS " + ctx.Ident(alias)) + (toSqlite ? string.Empty : Alias(null, t.ColumnAliases));
            case PgSubqueryRef s:
                if (s.Lateral && toSqlite)
                {
                    issues.Add("LATERAL is not supported in SQLite; rewrite as a correlated subquery");
                }

                return $"{(s.Lateral && !toSqlite ? "LATERAL " : string.Empty)}({Query(s.Query)}){Alias(s.Alias, toSqlite ? [] : s.ColumnAliases)}";
            case PgValuesRef v:
                return $"(VALUES {string.Join(", ", v.Rows.Select(r => "(" + string.Join(", ", r.Select(Expr)) + ")"))}){Alias(v.Alias, toSqlite ? [] : v.ColumnAliases)}";
            case PgFunctionRef fn:
                if (toSqlite && fn.Call.Function.Last.Equals("generate_series", StringComparison.OrdinalIgnoreCase))
                {
                    var column = fn.ColumnAliases.Count > 0 ? fn.ColumnAliases[0] : fn.Alias ?? "value";
                    var args = fn.Call.Args.Select(Expr).ToList();
                    return $"(WITH RECURSIVE trek_series(value) AS (SELECT {args[0]} UNION ALL SELECT value + {(args.Count > 2 ? args[2] : "1")} FROM trek_series WHERE value + {(args.Count > 2 ? args[2] : "1")} <= {args[1]}) SELECT value AS {ctx.Ident(column)} FROM trek_series){Alias(fn.Alias ?? "generate_series", [])}";
                }

                return $"{(fn.Lateral && !toSqlite ? "LATERAL " : string.Empty)}{Call(fn.Call)}{(fn.Ordinality && !toSqlite ? " WITH ORDINALITY" : string.Empty)}{Alias(fn.Alias, toSqlite ? [] : fn.ColumnAliases)}";
            case PgJoin { Kind: "right" } j when toSqlite:
            {
                // RIGHT JOIN needs SQLite 3.39: the same join as a LEFT JOIN with the sides swapped.
                var tail = j.On is not null ? " ON " + Expr(j.On) : j.Using.Count > 0 ? $" USING ({string.Join(", ", j.Using.Select(ctx.Ident))})" : string.Empty;
                var left = j.Left is PgJoin ? $"({From(j.Left)})" : From(j.Left);
                return $"{From(j.Right)} {(j.Natural ? "NATURAL " : string.Empty)}LEFT JOIN {left}{tail}";
            }

            case PgJoin j:
            {
                var kind = j.Kind switch
                {
                    "left" => "LEFT JOIN",
                    "right" => "RIGHT JOIN",
                    "full" => "FULL JOIN",
                    "cross" => "CROSS JOIN",
                    _ => "JOIN",
                };
                var tail = j.On is not null ? " ON " + Expr(j.On) : j.Using.Count > 0 ? $" USING ({string.Join(", ", j.Using.Select(ctx.Ident))})" : string.Empty;
                if (toSqlite && j.Kind == "full")
                {
                    tail += Note("FULL JOIN needs SQLite 3.39, newer than sqlite.minVersion (3.26): rewrite it as a LEFT JOIN plus the unmatched rows of the other side (UNION ALL), or raise sqlite.minVersion");
                }

                return $"{From(j.Left)} {(j.Natural ? "NATURAL " : string.Empty)}{kind} {From(j.Right)}{tail}";
            }

            default:
                return Unsupported("FROM item not translated", f.GetType().Name, string.Empty);
        }
    }

    // ------------------------------------------------------------------ statements

    public string Statement(PgStatement s) => s switch
    {
        PgSelectStatement sel => Query(sel.Query) + ";",
        PgInsert i => Insert(i),
        PgUpdate u => Update(u),
        PgDelete d => Delete(d),
        _ => Review.Comment("statement not translated", s.Source),
    };

    private string Returning(IReadOnlyList<PgTarget> returning) =>
        returning.Count == 0 ? string.Empty
        : toSqlite ? Note("RETURNING was left out: it needs SQLite 3.35, and SQLite does not allow it in a trigger")
        : " RETURNING " + string.Join(", ", returning.Select(t => Expr(t.Expr) + (t.Alias is null ? string.Empty : " AS " + ctx.Ident(t.Alias))));

    private string WithPrefix(IReadOnlyList<PgCte> with) =>
        with.Count == 0 ? string.Empty : Query(new PgQuery(with, false, new PgSelectCore(false, null, [], [], null, [], null), [], null, null)).Split(" SELECT NULL", 2)[0] + " ";

    public string Insert(PgInsert i, string? guard = null)
    {
        var columns = i.Columns.Count == 0 ? string.Empty : " (" + string.Join(", ", i.Columns.Select(ctx.Ident)) + ")";
        var verb = "INSERT";
        var conflict = string.Empty;
        if (i.OnConflict is { } onConflict)
        {
            if (onConflict.StartsWith("OR ", StringComparison.Ordinal))
            {
                if (toSqlite)
                {
                    verb = "INSERT " + onConflict;
                }
                else if (onConflict == "OR IGNORE")
                {
                    conflict = " ON CONFLICT DO NOTHING";
                }
                else
                {
                    conflict = Note($"INSERT {onConflict} has no direct counterpart: use ON CONFLICT (...) DO UPDATE");
                }
            }
            else
            {
                conflict = " " + onConflict;
            }
        }

        string source;
        if (i.DefaultValues)
        {
            source = "DEFAULT VALUES";
        }
        else if (guard is not null)
        {
            source = i.Rows!.Body is PgValuesBody { Rows: [var row] }
                ? $"SELECT {string.Join(", ", row.Select(Expr))} WHERE {guard}"
                : $"SELECT * FROM ({Query(i.Rows)}) WHERE {guard}";
        }
        else
        {
            source = Query(i.Rows!);
        }

        return $"{WithPrefix(i.With)}{verb} INTO {Table(i.Table)}{(i.Alias is null ? string.Empty : " AS " + ctx.Ident(i.Alias))}{columns} {source}{conflict}{Returning(i.Returning)};";
    }

    public string Update(PgUpdate u, string? guard = null) => WithoutTargetAlias(u.Alias, u.Table, alias => Update(u, guard, alias));

    private string Update(PgUpdate u, string? guard, string alias)
    {
        if (toSqlite && u.From.Count > 0)
        {
            // UPDATE ... FROM needs SQLite 3.33: each value as a correlated subquery, for the rows that have a match.
            var sources = string.Join(", ", u.From.Select(From));
            var match = u.Where is null ? string.Empty : " WHERE " + Expr(u.Where);
            var values = string.Join(", ", u.Sets.Select(s => $"{ctx.Ident(s.Column)} = (SELECT {Expr(s.Value)} FROM {sources}{match})"));
            var exists = $"EXISTS (SELECT 1 FROM {sources}{match})";
            return $"{WithPrefix(u.With)}UPDATE {Table(u.Table)}{alias} SET {values}{Where(new PgRawExpr(exists), guard, raw: exists)}{Returning(u.Returning)};";
        }

        var sets = string.Join(", ", u.Sets.Select(s => $"{ctx.Ident(s.Column)} = {Expr(s.Value)}"));
        var where = Where(u.Where, guard);
        var from = u.From.Count == 0 ? string.Empty : " FROM " + string.Join(", ", u.From.Select(From));
        return $"{WithPrefix(u.With)}UPDATE {Table(u.Table)}{alias} SET {sets}{from}{where}{Returning(u.Returning)};";
    }

    public string Delete(PgDelete d, string? guard = null) => WithoutTargetAlias(d.Alias, d.Table, alias => Delete(d, guard, alias));

    private string Delete(PgDelete d, string? guard, string alias)
    {
        if (d.Using.Count > 0 && toSqlite)
        {
            // SQLite has no DELETE ... USING: keep the rows that have a match.
            var exists = $"EXISTS (SELECT 1 FROM {string.Join(", ", d.Using.Select(From))}{(d.Where is null ? string.Empty : " WHERE " + Expr(d.Where))})";
            return $"{WithPrefix(d.With)}DELETE FROM {Table(d.Table)}{alias}{Where(new PgRawExpr(exists), guard, raw: exists)}{Returning(d.Returning)};";
        }

        var usingList = d.Using.Count == 0 ? string.Empty : " USING " + string.Join(", ", d.Using.Select(From));
        return $"{WithPrefix(d.With)}DELETE FROM {Table(d.Table)}{alias}{usingList}{Where(d.Where, guard)}{Returning(d.Returning)};";
    }

    private string Where(PgExpr? where, string? guard, string? raw = null)
    {
        var condition = raw ?? (where is null ? null : Expr(where));
        return (condition, guard) switch
        {
            (null, null) => string.Empty,
            (null, _) => " WHERE " + guard,
            (_, null) => " WHERE " + condition,
            _ => $" WHERE ({condition}) AND {guard}",
        };
    }
}
