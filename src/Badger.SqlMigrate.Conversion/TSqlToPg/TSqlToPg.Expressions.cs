using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Badger.SqlMigrate.Conversion.TSqlToPg;

/// <summary>
/// Translates T-SQL into PostgreSQL by walking ScriptDom's syntax tree. Expressions, queries and DML are rewritten
/// construct by construct; anything without a faithful counterpart is kept as a REVIEW comment so it is never lost
/// silently. Column and variable types are only partly known (see <see cref="ConversionContext.ColumnKindOf"/>), so
/// "+" becomes "||" only when one side is known to be text.
/// </summary>
internal sealed partial class TSqlToPgTranslator(ConversionContext ctx) : ITranslator
{
    /// <summary>State for the object being translated.</summary>
    private Scope scope = new("expression");

    private sealed class Scope(string owner)
    {
        public string Owner { get; } = owner;

        public List<string> Issues { get; } = [];

        /// <summary>T-SQL variable (lowercase, with @) → PL/pgSQL name, type and kind.</summary>
        public Dictionary<string, (string Name, string Type, ConversionContext.ColumnKind Kind)> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool UsesRowCount { get; set; }

        /// <summary>Inside a trigger: the table and which transition tables exist for the event being written.</summary>
        public string? TriggerTable { get; set; }

        public bool HasInserted { get; set; }

        public bool HasDeleted { get; set; }

        /// <summary>Temporary tables (#t, @t) created in the body; references drop the prefix.</summary>
        public HashSet<string> TempTables { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ entry points

    public string Expression(string expression, ExpressionSite site)
    {
        scope = new Scope(site.Owner);
        var parsed = Parser().ParseExpression(new StringReader(expression), out var errors);
        if (errors.Count > 0 || parsed is null)
        {
            ctx.Issue(site.Owner, $"could not parse {expression}: {errors.FirstOrDefault()?.Message}");
            return "NULL " + Review.Comment("could not translate", expression);
        }

        var result = Expr(Unwrap(parsed));
        if (site.TargetType is "boolean" && parsed is not null && Unwrap(parsed) is IntegerLiteral { Value: "0" or "1" } bit)
        {
            result = bit.Value == "1" ? "true" : "false";
        }

        Flush();
        return result;
    }

    public string Predicate(string predicate, ExpressionSite site)
    {
        scope = new Scope(site.Owner);
        var parsed = Parser().ParseBooleanExpression(new StringReader(predicate), out var errors);
        if (errors.Count > 0 || parsed is null)
        {
            ctx.Issue(site.Owner, $"could not parse {predicate}: {errors.FirstOrDefault()?.Message}");
            return "true " + Review.Comment("could not translate", predicate);
        }

        var result = Bool(UnwrapBool(parsed));
        Flush();
        return result;
    }

    private void Flush()
    {
        foreach (var issue in scope.Issues)
        {
            ctx.Issue(scope.Owner, issue);
        }
    }

    internal static TSql170Parser Parser() => new(initialQuotedIdentifiers: true);

    private static ScalarExpression Unwrap(ScalarExpression e) => e is ParenthesisExpression p ? Unwrap(p.Expression) : e;

    private static BooleanExpression UnwrapBool(BooleanExpression e) => e is BooleanParenthesisExpression p ? UnwrapBool(p.Expression) : e;

    /// <summary>The original text of a fragment.</summary>
    private static string Text(TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream is null || fragment.FirstTokenIndex < 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
        {
            sb.Append(fragment.ScriptTokenStream[i].Text);
        }

        return sb.ToString();
    }

    /// <summary>Marks a fragment that has no translation; returns a REVIEW comment (prefixed by a placeholder where SQL needs a value).</summary>
    private string Unsupported(TSqlFragment fragment, string why, string placeholder = "NULL")
    {
        scope.Issues.Add($"{why}: {Collapse(Text(fragment))}");
        return (placeholder.Length == 0 ? string.Empty : placeholder + " ") + Review.Comment(why, Text(fragment));
    }

    private string Note(string message)
    {
        scope.Issues.Add(message);
        return " " + Review.Comment(message);
    }

    private static string Collapse(string text)
    {
        var collapsed = WhitespaceRegex().Replace(text, " ").Trim();
        return collapsed.Length > 120 ? collapsed[..117] + "..." : collapsed;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // ------------------------------------------------------------------ names

    private string Id(Identifier identifier) => ctx.Ident(identifier.Value);

    /// <summary>schema.object with the default schema mapped; temp tables (#t) lose their prefix.</summary>
    private string ObjectName(SchemaObjectName name)
    {
        if (name.DatabaseIdentifier is not null || name.ServerIdentifier is not null)
        {
            scope.Issues.Add($"cross-database reference {Text(name)}: the database part was dropped");
        }

        var baseName = name.BaseIdentifier.Value;
        if (baseName.StartsWith('#'))
        {
            return ctx.Ident(baseName.TrimStart('#'));
        }

        return name.SchemaIdentifier is null ? ctx.Ident(baseName) : $"{ctx.Quote(ctx.Names.Schema(name.SchemaIdentifier.Value))}.{ctx.Ident(baseName)}";
    }

    private string Var(string tsqlName)
    {
        if (scope.Variables.TryGetValue(tsqlName, out var v))
        {
            return v.Name;
        }

        // Undeclared (e.g. a parameter of an expression translated on its own): keep a readable name.
        return ctx.Quote(VariableName(tsqlName, "v_"));
    }

    private string VariableName(string tsqlName, string prefix) => prefix + NameMapper.Apply(tsqlName.TrimStart('@'), ctx.Names.Style == NameStyle.Keep ? NameStyle.Lower : ctx.Names.Style);

    // ------------------------------------------------------------------ scalar expressions

    private string Expr(ScalarExpression e) => e switch
    {
        ParenthesisExpression p => "(" + Expr(p.Expression) + ")",
        IntegerLiteral l => l.Value,
        NumericLiteral l => l.Value,
        RealLiteral l => l.Value,
        MoneyLiteral l => l.Value.TrimStart('$'),
        StringLiteral s => ctx.Literal(s.Value),
        NullLiteral => "NULL",
        DefaultLiteral => "DEFAULT",
        BinaryLiteral b => $"'\\x{b.Value[2..]}'::bytea",
        IdentifierLiteral i => i.Value,
        VariableReference v => Var(v.Name),
        GlobalVariableExpression g => GlobalVariable(g),
        ColumnReferenceExpression c => Column(c),
        FunctionCall f => Function(f),
        BinaryExpression b => Binary(b),
        UnaryExpression u => (u.UnaryExpressionType switch { UnaryExpressionType.Negative => "-", UnaryExpressionType.BitwiseNot => "~", _ => "+" }) + Expr(u.Expression),
        TryCastCall c => $"CAST({Expr(c.Parameter)} AS {Type(c.DataType)})" + Note("TRY_CAST becomes CAST: a value that does not convert now raises an error instead of returning NULL"),
        CastCall c => $"CAST({Expr(c.Parameter)} AS {Type(c.DataType)})",
        TryConvertCall c => Convert(c.DataType, c.Parameter, c.Style) + Note("TRY_CONVERT becomes a conversion that raises an error instead of returning NULL"),
        ConvertCall c => Convert(c.DataType, c.Parameter, c.Style),
        SearchedCaseExpression c => "CASE " + string.Concat(c.WhenClauses.Select(w => $"WHEN {Bool(w.WhenExpression)} THEN {Expr(w.ThenExpression)} ")) + (c.ElseExpression is null ? string.Empty : $"ELSE {Expr(c.ElseExpression)} ") + "END",
        SimpleCaseExpression c => $"CASE {Expr(c.InputExpression)} " + string.Concat(c.WhenClauses.Select(w => $"WHEN {Expr(w.WhenExpression)} THEN {Expr(w.ThenExpression)} ")) + (c.ElseExpression is null ? string.Empty : $"ELSE {Expr(c.ElseExpression)} ") + "END",
        CoalesceExpression c => $"COALESCE({string.Join(", ", c.Expressions.Select(Expr))})",
        NullIfExpression n => $"NULLIF({Expr(n.FirstExpression)}, {Expr(n.SecondExpression)})",
        IIfCall i => $"CASE WHEN {Bool(i.Predicate)} THEN {Expr(i.ThenExpression)} ELSE {Expr(i.ElseExpression)} END",
        ScalarSubquery s => "(" + Query(s.QueryExpression) + ")",
        LeftFunctionCall l => $"left({string.Join(", ", l.Parameters.Select(Expr))})",
        RightFunctionCall r => $"right({string.Join(", ", r.Parameters.Select(Expr))})",
        NextValueForExpression n => $"nextval({ctx.Literal(Unquoted(ObjectName(n.SequenceName)))})",
        ParameterlessCall p => p.ParameterlessCallType switch
        {
            ParameterlessCallType.CurrentTimestamp => "LOCALTIMESTAMP",
            ParameterlessCallType.SessionUser or ParameterlessCallType.SystemUser => "session_user",
            _ => "current_user",
        },
        AtTimeZoneCall a => $"({Expr(a.DateValue)} AT TIME ZONE {Expr(a.TimeZone)})" + Note("AT TIME ZONE: SQL Server takes Windows time zone names, PostgreSQL IANA names"),
        _ => Unsupported(e, "expression not translated"),
    };

    private static string Unquoted(string qualified) => qualified.Replace("\"", string.Empty, StringComparison.Ordinal);

    private string GlobalVariable(GlobalVariableExpression g)
    {
        switch (g.Name.ToUpperInvariant())
        {
            case "@@ROWCOUNT":
                scope.UsesRowCount = true;
                return "v_rowcount";
            case "@@IDENTITY":
                return "lastval()" + Note("@@IDENTITY becomes lastval(): the last value of any sequence this session used");
            case "@@SPID":
                return "pg_backend_pid()";
            case "@@VERSION":
                return "version()";
            case "@@SERVERNAME":
                return "inet_server_addr()::text";
            case "@@TRANCOUNT":
                return "1" + Note("@@TRANCOUNT has no PostgreSQL counterpart");
            default:
                return Unsupported(g, $"{g.Name} has no PostgreSQL counterpart");
        }
    }

    private string Column(ColumnReferenceExpression c)
    {
        if (c.ColumnType == ColumnType.Wildcard)
        {
            return c.MultiPartIdentifier is null ? "*" : Parts(c.MultiPartIdentifier.Identifiers) + ".*";
        }

        if (c.ColumnType is ColumnType.IdentityCol or ColumnType.RowGuidCol)
        {
            return Unsupported(c, "$IDENTITY / $ROWGUID column references are not translated");
        }

        return Parts(c.MultiPartIdentifier.Identifiers);
    }

    /// <summary>a, alias.col, schema.table.col (database parts are dropped).</summary>
    private string Parts(IList<Identifier> parts)
    {
        var list = parts.ToList();
        if (list.Count >= 4)
        {
            scope.Issues.Add($"cross-database reference {string.Join('.', list.Select(p => p.Value))}: the database part was dropped");
            list = list.Skip(list.Count - 3).ToList();
        }

        if (list.Count == 3)
        {
            return $"{ctx.Quote(ctx.Names.Schema(list[0].Value))}.{Id(list[1])}.{Id(list[2])}";
        }

        return string.Join('.', list.Select(p => p.Value.StartsWith('#') ? ctx.Ident(p.Value.TrimStart('#')) : Id(p)));
    }

    private string Binary(BinaryExpression b)
    {
        var op = b.BinaryExpressionType switch
        {
            BinaryExpressionType.Add => IsString(b.FirstExpression) || IsString(b.SecondExpression) ? "||" : "+",
            BinaryExpressionType.Subtract => "-",
            BinaryExpressionType.Multiply => "*",
            BinaryExpressionType.Divide => "/",
            BinaryExpressionType.Modulo => "%",
            BinaryExpressionType.BitwiseAnd => "&",
            BinaryExpressionType.BitwiseOr => "|",
            BinaryExpressionType.BitwiseXor => "#",
            _ => "+",
        };

        return $"{Expr(b.FirstExpression)} {op} {Expr(b.SecondExpression)}";
    }

    private static readonly HashSet<string> StringFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONCAT", "CONCAT_WS", "SUBSTRING", "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "REPLACE", "REPLICATE", "STUFF", "FORMAT", "STR", "CHAR", "NCHAR",
        "SPACE", "QUOTENAME", "REVERSE", "STRING_AGG", "DATENAME", "TRANSLATE", "STRING_ESCAPE", "JSON_VALUE", "JSON_QUERY",
    };

    /// <summary>True when the expression is known to be character data.</summary>
    private bool IsString(ScalarExpression e) => e switch
    {
        StringLiteral => true,
        ParenthesisExpression p => IsString(p.Expression),
        BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } b => IsString(b.FirstExpression) || IsString(b.SecondExpression),
        FunctionCall f => StringFunctions.Contains(f.FunctionName.Value) || ((f.FunctionName.Value.Equals("ISNULL", StringComparison.OrdinalIgnoreCase) || f.FunctionName.Value.Equals("COALESCE", StringComparison.OrdinalIgnoreCase)) && f.Parameters.Any(IsString)),
        LeftFunctionCall or RightFunctionCall => true,
        CoalesceExpression c => c.Expressions.Any(IsString),
        CastCall c => IsTextType(c.DataType),
        ConvertCall c => IsTextType(c.DataType),
        SearchedCaseExpression c => c.WhenClauses.Any(w => IsString(w.ThenExpression)) || (c.ElseExpression is not null && IsString(c.ElseExpression)),
        SimpleCaseExpression c => c.WhenClauses.Any(w => IsString(w.ThenExpression)) || (c.ElseExpression is not null && IsString(c.ElseExpression)),
        IIfCall i => IsString(i.ThenExpression) || IsString(i.ElseExpression),
        VariableReference v => scope.Variables.TryGetValue(v.Name, out var info) && info.Kind == ConversionContext.ColumnKind.Text,
        ColumnReferenceExpression { ColumnType: ColumnType.Regular } c => ctx.ColumnKindOf(c.MultiPartIdentifier.Identifiers[^1].Value) == ConversionContext.ColumnKind.Text,
        _ => false,
    };

    /// <summary>True when the expression is known to be a bit (boolean on PostgreSQL).</summary>
    private bool IsBit(ScalarExpression e) => e switch
    {
        ParenthesisExpression p => IsBit(p.Expression),
        VariableReference v => scope.Variables.TryGetValue(v.Name, out var info) && info.Kind == ConversionContext.ColumnKind.Boolean,
        ColumnReferenceExpression { ColumnType: ColumnType.Regular } c => ctx.ColumnKindOf(c.MultiPartIdentifier.Identifiers[^1].Value) == ConversionContext.ColumnKind.Boolean,
        CastCall c => Text(c.DataType).Trim('[', ']').Equals("bit", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool IsTextType(DataTypeReference type) =>
        type is SqlDataTypeReference s && s.SqlDataTypeOption is SqlDataTypeOption.Char or SqlDataTypeOption.VarChar or SqlDataTypeOption.NChar or SqlDataTypeOption.NVarChar or SqlDataTypeOption.Text or SqlDataTypeOption.NText;

    /// <summary>A value assigned to or compared with a boolean: 0/1 become false/true.</summary>
    private string Value(ScalarExpression value, bool boolean) =>
        boolean && Unwrap(value) is IntegerLiteral { Value: "0" or "1" } l ? (l.Value == "1" ? "true" : "false") : Expr(value);

    private string Type(DataTypeReference type)
    {
        var text = Text(type).Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
        var mapping = TypeMapper.TSqlToPg(text, ctx);
        if (mapping.Note is not null)
        {
            scope.Issues.Add($"{text}: {mapping.Note}");
        }

        return mapping.Type;
    }

    private static readonly Dictionary<int, string> DateStyles = new()
    {
        [23] = "YYYY-MM-DD", [112] = "YYYYMMDD", [120] = "YYYY-MM-DD HH24:MI:SS", [121] = "YYYY-MM-DD HH24:MI:SS.MS", [126] = "YYYY-MM-DD\"T\"HH24:MI:SS.MS",
        [127] = "YYYY-MM-DD\"T\"HH24:MI:SS.MS", [101] = "MM/DD/YYYY", [103] = "DD/MM/YYYY", [104] = "DD.MM.YYYY", [108] = "HH24:MI:SS", [111] = "YYYY/MM/DD",
    };

    private string Convert(DataTypeReference type, ScalarExpression value, ScalarExpression? style)
    {
        if (style is null)
        {
            return $"CAST({Expr(value)} AS {Type(type)})";
        }

        if (IsTextType(type) && Unwrap(style) is IntegerLiteral l && DateStyles.TryGetValue(int.Parse(l.Value, CultureInfo.InvariantCulture), out var format))
        {
            return $"to_char({Expr(value)}, {ctx.Literal(format)})";
        }

        return $"CAST({Expr(value)} AS {Type(type)})" + Note($"CONVERT style {Text(style)} is not translated");
    }

    // ------------------------------------------------------------------ functions

    private string Function(FunctionCall f)
    {
        var args = f.Parameters.ToList();
        string A(int i) => Expr(args[i]);
        string All() => string.Join(", ", args.Select(Expr));

        if (f.CallTarget is MultiPartIdentifierCallTarget target)
        {
            // schema.function(...): a user function.
            var schemaName = target.MultiPartIdentifier.Identifiers[^1].Value;
            return $"{ctx.Quote(ctx.Names.Schema(schemaName))}.{Id(f.FunctionName)}({All()})" + Over(f);
        }

        if (f.CallTarget is not null)
        {
            return Unsupported(f, "method call not translated");
        }

        var name = f.FunctionName.Value.ToUpperInvariant();
        var distinct = f.UniqueRowFilter == UniqueRowFilter.Distinct ? "DISTINCT " : string.Empty;
        var result = name switch
        {
            "ISNULL" => $"COALESCE({All()})",
            "GETDATE" or "SYSDATETIME" => "LOCALTIMESTAMP",
            "GETUTCDATE" or "SYSUTCDATETIME" => "(now() AT TIME ZONE 'utc')",
            "SYSDATETIMEOFFSET" => "now()",
            "NEWID" => "gen_random_uuid()",
            "NEWSEQUENTIALID" => "gen_random_uuid()" + Note("NEWSEQUENTIALID becomes gen_random_uuid(): values are not sequential"),
            "LEN" => $"char_length(rtrim({A(0)}))",
            "DATALENGTH" => $"octet_length({A(0)})",
            "CHARINDEX" when args.Count == 2 => $"strpos({A(1)}, {A(0)})",
            "CHARINDEX" => $"(strpos(substr({A(1)}, {A(2)}), {A(0)}) + CASE WHEN strpos(substr({A(1)}, {A(2)}), {A(0)}) > 0 THEN {A(2)} - 1 ELSE 0 END)",
            "PATINDEX" => Unsupported(f, "PATINDEX uses T-SQL patterns; rewrite with a regular expression"),
            "SUBSTRING" => $"substring({A(0)}, {A(1)}, {A(2)})",
            "REPLICATE" => $"repeat({A(0)}, {A(1)})",
            "SPACE" => $"repeat(' ', {A(0)})",
            "STUFF" => $"overlay({A(0)} placing {A(3)} from {A(1)} for {A(2)})",
            "STR" => $"CAST({A(0)} AS text)",
            "CHAR" or "NCHAR" => $"chr({A(0)})",
            "ASCII" or "UNICODE" => $"ascii({A(0)})",
            "QUOTENAME" => $"quote_ident({A(0)})",
            "UPPER" or "LOWER" or "LTRIM" or "RTRIM" or "TRIM" or "REPLACE" or "REVERSE" or "CONCAT" or "CONCAT_WS" or "TRANSLATE" or "ABS" or "FLOOR" or "SIGN" or "SQRT"
                or "EXP" or "PI" or "POWER" or "SQUARE" or "SIN" or "COS" or "TAN" or "ASIN" or "ACOS" or "ATAN" or "DEGREES" or "RADIANS" or "GREATEST" or "LEAST"
                or "SUM" or "AVG" or "MIN" or "MAX" or "ROW_NUMBER" or "RANK" or "DENSE_RANK" or "NTILE" or "LAG" or "LEAD" or "FIRST_VALUE" or "LAST_VALUE"
                or "PERCENT_RANK" or "CUME_DIST" or "STDEV" or "VAR"
                => $"{(name switch { "SQUARE" => "power", "STDEV" => "stddev_samp", "VAR" => "var_samp", _ => name.ToLowerInvariant() })}({distinct}{All()}{(name == "SQUARE" ? ", 2" : string.Empty)})",
            "COUNT" or "COUNT_BIG" => args.Count == 1 && args[0] is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard } ? "count(*)" : $"count({distinct}{All()})",
            "CEILING" => $"ceil({A(0)})",
            "ROUND" when args.Count >= 3 => $"trunc({A(0)}, {A(1)})",
            "ROUND" => $"round({All()})",
            "LOG" when args.Count == 2 => $"log({A(1)}, {A(0)})",
            "LOG" => $"ln({A(0)})",
            "LOG10" => $"log({A(0)})",
            "ATN2" => $"atan2({A(0)}, {A(1)})",
            "RAND" => "random()",
            "STRING_AGG" => $"string_agg({A(0)}, {A(1)}{WithinGroup(f)})",
            "DB_NAME" => "current_database()",
            "USER_NAME" or "SUSER_NAME" or "SUSER_SNAME" or "ORIGINAL_LOGIN" => "current_user",
            "HOST_NAME" => "inet_client_addr()::text",
            "SCOPE_IDENTITY" or "IDENT_CURRENT" => "lastval()" + Note($"{name} becomes lastval(): the last sequence value this session used; prefer INSERT ... RETURNING"),
            "YEAR" or "MONTH" or "DAY" => $"CAST(date_part('{name.ToLowerInvariant()}', {A(0)}) AS integer)",
            "DATEPART" => $"CAST(date_part('{DatePart(args[0], f)}', {A(1)}) AS integer)",
            "DATENAME" => DateName(args, f),
            "DATEADD" => $"({A(2)} + ({A(1)}) * interval '1 {DatePart(args[0], f)}')",
            "DATEDIFF" or "DATEDIFF_BIG" => DateDiff(DatePart(args[0], f), A(1), A(2)),
            "EOMONTH" => $"CAST(date_trunc('month', {A(0)}) + interval '1 month' - interval '1 day' AS date)",
            "DATEFROMPARTS" => $"make_date({All()})",
            "DATETIMEFROMPARTS" or "DATETIME2FROMPARTS" => $"make_timestamp({A(0)}, {A(1)}, {A(2)}, {A(3)}, {A(4)}, {A(5)})",
            "ISNUMERIC" => $"CASE WHEN CAST({A(0)} AS text) ~ '^\\s*[-+]?([0-9]+\\.?[0-9]*|\\.[0-9]+)\\s*$' THEN 1 ELSE 0 END",
            "ISJSON" => $"CASE WHEN ({A(0)}) IS JSON THEN 1 ELSE 0 END" + Note("IS JSON needs PostgreSQL 16"),
            "JSON_VALUE" => $"(jsonb_path_query_first(CAST({A(0)} AS jsonb), CAST({A(1)} AS jsonpath)) #>> '{{}}')",
            "JSON_QUERY" => $"CAST(jsonb_path_query_first(CAST({A(0)} AS jsonb), CAST({A(1)} AS jsonpath)) AS text)",
            "FORMAT" => $"to_char({A(0)}, {(args.Count > 1 ? A(1) : "''")})" + Note("FORMAT uses .NET format strings; check the to_char pattern"),
            "CHOOSE" => "CASE " + A(0) + string.Concat(args.Skip(1).Select((a, i) => $" WHEN {i + 1} THEN {Expr(a)}")) + " END",
            "OBJECT_ID" => $"to_regclass({A(0)})" + Note("OBJECT_ID becomes to_regclass, which returns an oid, not an int"),
            "ERROR_MESSAGE" => "SQLERRM",
            "ERROR_NUMBER" => "SQLSTATE" + Note("ERROR_NUMBER becomes SQLSTATE, a five-character code"),
            "ERROR_SEVERITY" or "ERROR_STATE" or "ERROR_LINE" or "ERROR_PROCEDURE" => "NULL" + Note($"{name} has no PostgreSQL counterpart"),
            "CAST" or "CONVERT" => Unsupported(f, "conversion not translated"),
            "COLLATIONPROPERTY" or "SERVERPROPERTY" or "DATABASEPROPERTYEX" or "OBJECT_NAME" or "OBJECTPROPERTY" or "COLUMNPROPERTY" or "HAS_PERMS_BY_NAME"
                or "IS_MEMBER" or "IS_SRVROLEMEMBER" or "CONTEXT_INFO" or "SESSION_CONTEXT" or "XACT_STATE" or "@@ERROR" => Unsupported(f, $"{name} has no PostgreSQL counterpart"),
            _ => $"{ctx.Ident(f.FunctionName.Value)}({distinct}{All()})" + Note($"{f.FunctionName.Value} was kept as written; check that PostgreSQL has it"),
        };

        return result + Over(f);
    }

    private string WithinGroup(FunctionCall f) =>
        f.WithinGroupClause?.OrderByClause is { } order ? " " + OrderBy(order) : string.Empty;

    private string Over(FunctionCall f)
    {
        if (f.OverClause is not { } over)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (over.Partitions.Count > 0)
        {
            parts.Add("PARTITION BY " + string.Join(", ", over.Partitions.Select(Expr)));
        }

        if (over.OrderByClause is { } order)
        {
            parts.Add(OrderBy(order));
        }

        if (over.WindowFrameClause is { } frame)
        {
            parts.Add(Text(frame));
        }

        return $" OVER ({string.Join(' ', parts)})";
    }

    private string DatePart(ScalarExpression part, TSqlFragment owner)
    {
        var p = Text(part).Trim('\'', '[', ']').ToLowerInvariant();
        return p switch
        {
            "year" or "yy" or "yyyy" => "year",
            "quarter" or "qq" or "q" => "quarter",
            "month" or "mm" or "m" => "month",
            "dayofyear" or "dy" or "y" => "doy",
            "day" or "dd" or "d" => "day",
            "week" or "wk" or "ww" => "week",
            "iso_week" or "isowk" or "isoww" => "week",
            "weekday" or "dw" => "dow",
            "hour" or "hh" => "hour",
            "minute" or "mi" or "n" => "minute",
            "second" or "ss" or "s" => "second",
            "millisecond" or "ms" => "milliseconds",
            "microsecond" or "mcs" => "microseconds",
            _ => Fallback(),
        };

        string Fallback()
        {
            scope.Issues.Add($"date part {p} in {Collapse(Text(owner))} is not translated");
            return p;
        }
    }

    private string DateName(List<ScalarExpression> args, FunctionCall f)
    {
        var part = DatePart(args[0], f);
        return part switch
        {
            "month" => $"trim(to_char({Expr(args[1])}, 'Month'))",
            "dow" => $"trim(to_char({Expr(args[1])}, 'Day'))",
            _ => $"CAST(date_part('{part}', {Expr(args[1])}) AS text)",
        };
    }

    /// <summary>DATEDIFF counts boundaries crossed; these are exact for day and coarser, and for whole hours/minutes/seconds of timestamps.</summary>
    private static string DateDiff(string part, string a, string b) => part switch
    {
        "year" => $"CAST(date_part('year', {b}) - date_part('year', {a}) AS integer)",
        "quarter" => $"CAST((date_part('year', {b}) - date_part('year', {a})) * 4 + date_part('quarter', {b}) - date_part('quarter', {a}) AS integer)",
        "month" => $"CAST((date_part('year', {b}) - date_part('year', {a})) * 12 + date_part('month', {b}) - date_part('month', {a}) AS integer)",
        "day" or "doy" => $"(CAST({b} AS date) - CAST({a} AS date))",
        "week" => $"CAST(floor((CAST({b} AS date) - CAST({a} AS date)) / 7.0) AS integer)",
        "hour" => $"CAST(floor(extract(epoch FROM date_trunc('hour', {b}) - date_trunc('hour', {a})) / 3600) AS integer)",
        "minute" => $"CAST(floor(extract(epoch FROM date_trunc('minute', {b}) - date_trunc('minute', {a})) / 60) AS integer)",
        "second" => $"CAST(floor(extract(epoch FROM date_trunc('second', {b}) - date_trunc('second', {a}))) AS integer)",
        "milliseconds" => $"CAST(floor(extract(epoch FROM {b} - {a}) * 1000) AS bigint)",
        "microseconds" => $"CAST(floor(extract(epoch FROM {b} - {a}) * 1000000) AS bigint)",
        _ => $"CAST(extract(epoch FROM {b} - {a}) AS bigint)",
    };

    // ------------------------------------------------------------------ boolean expressions

    private string Bool(BooleanExpression b)
    {
        switch (b)
        {
            case BooleanParenthesisExpression p:
                return "(" + Bool(p.Expression) + ")";
            case BooleanBinaryExpression x:
                return $"{Bool(x.FirstExpression)} {(x.BinaryExpressionType == BooleanBinaryExpressionType.And ? "AND" : "OR")} {Bool(x.SecondExpression)}";
            case BooleanNotExpression n:
                return "NOT " + Bool(n.Expression);
            case BooleanComparisonExpression c:
            {
                var op = c.ComparisonType switch
                {
                    BooleanComparisonType.Equals => "=",
                    BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "<>",
                    BooleanComparisonType.GreaterThan => ">",
                    BooleanComparisonType.LessThan => "<",
                    BooleanComparisonType.GreaterThanOrEqualTo or BooleanComparisonType.NotLessThan => ">=",
                    BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.NotGreaterThan => "<=",
                    _ => null,
                };
                if (op is null)
                {
                    return Unsupported(c, "outer-join comparison (*=) is not translated", "true");
                }

                var boolean = IsBit(c.FirstExpression) || IsBit(c.SecondExpression);
                return $"{Value(c.FirstExpression, boolean)} {op} {Value(c.SecondExpression, boolean)}";
            }

            case BooleanIsNullExpression n:
                return $"{Expr(n.Expression)} IS {(n.IsNot ? "NOT " : string.Empty)}NULL";
            case InPredicate i:
            {
                var boolean = IsBit(i.Expression);
                var list = i.Subquery is not null ? Query(i.Subquery.QueryExpression) : string.Join(", ", i.Values.Select(v => Value(v, boolean)));
                return $"{Expr(i.Expression)} {(i.NotDefined ? "NOT " : string.Empty)}IN ({list})";
            }

            case LikePredicate l:
            {
                var pattern = l.SecondExpression;
                var note = Unwrap(pattern) is StringLiteral s && s.Value.Contains('[', StringComparison.Ordinal)
                    ? Note("LIKE pattern uses [...] character classes, which PostgreSQL LIKE does not support; use SIMILAR TO or ~")
                    : string.Empty;
                var left = ctx.CaseInsensitiveCollation ? $"{Expr(l.FirstExpression)} COLLATE \"default\"" : Expr(l.FirstExpression);
                var op = ctx.CaseInsensitiveCollation ? "ILIKE" : "LIKE";
                return $"{left} {(l.NotDefined ? "NOT " : string.Empty)}{op} {Expr(pattern)}{(l.EscapeExpression is null ? string.Empty : $" ESCAPE {Expr(l.EscapeExpression)}")}{note}";
            }

            case BooleanTernaryExpression t:
                return $"{Expr(t.FirstExpression)} {(t.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween ? "NOT " : string.Empty)}BETWEEN {Expr(t.SecondExpression)} AND {Expr(t.ThirdExpression)}";
            case ExistsPredicate e:
                return $"EXISTS ({Query(e.Subquery.QueryExpression)})";
            case SubqueryComparisonPredicate s:
            {
                var op = s.ComparisonType switch
                {
                    BooleanComparisonType.Equals => "=",
                    BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "<>",
                    BooleanComparisonType.GreaterThan => ">",
                    BooleanComparisonType.LessThan => "<",
                    BooleanComparisonType.GreaterThanOrEqualTo => ">=",
                    _ => "<=",
                };
                return $"{Expr(s.Expression)} {op} {(s.SubqueryComparisonPredicateType == SubqueryComparisonPredicateType.All ? "ALL" : "ANY")} ({Query(s.Subquery.QueryExpression)})";
            }

            case DistinctPredicate d:
                return $"{Expr(d.FirstExpression)} IS {(d.IsNot ? "NOT " : string.Empty)}DISTINCT FROM {Expr(d.SecondExpression)}";
            case UpdateCall u:
                return "true " + Review.Comment($"UPDATE({u.Identifier.Value}) is always true here; compare inserted and deleted to see whether the column changed") + Track($"UPDATE({u.Identifier.Value}) needs rewriting");
            default:
                return Unsupported(b, "condition not translated", "true");
        }
    }

    private string Track(string issue)
    {
        scope.Issues.Add(issue);
        return string.Empty;
    }

    // ------------------------------------------------------------------ queries

    /// <param name="selectList">Replaces the select list (SELECT x INTO v ...).</param>
    private string Query(QueryExpression q, string? selectList = null)
    {
        var sb = new StringBuilder();
        switch (q)
        {
            case QuerySpecification s:
                sb.Append(Select(s, selectList));
                break;
            case BinaryQueryExpression b:
                sb.Append(Query(b.FirstQueryExpression));
                sb.Append(b.BinaryQueryExpressionType switch
                {
                    BinaryQueryExpressionType.Except => " EXCEPT ",
                    BinaryQueryExpressionType.Intersect => " INTERSECT ",
                    _ => " UNION ",
                });
                if (b.All)
                {
                    sb.Append("ALL ");
                }

                sb.Append(Query(b.SecondQueryExpression));
                break;
            case QueryParenthesisExpression p:
                sb.Append('(').Append(Query(p.QueryExpression)).Append(')');
                break;
            default:
                return Unsupported(q, "query not translated", "SELECT NULL");
        }

        if (q.OrderByClause is { } order)
        {
            sb.Append(' ').Append(OrderBy(order));
        }

        if (q is QuerySpecification { TopRowFilter: { } top })
        {
            if (top.Percent)
            {
                sb.Append(' ').Append(Review.Comment("TOP ... PERCENT is not translated", Text(top)));
                scope.Issues.Add("TOP ... PERCENT is not translated");
            }
            else if (top.WithTies)
            {
                sb.Append($" FETCH FIRST {Expr(top.Expression)} ROWS WITH TIES");
            }
            else
            {
                sb.Append($" LIMIT {Expr(top.Expression)}");
            }
        }

        if (q.OffsetClause is { } offset)
        {
            sb.Append($" OFFSET {Expr(offset.OffsetExpression)} ROWS");
            if (offset.FetchExpression is not null)
            {
                sb.Append($" FETCH NEXT {Expr(offset.FetchExpression)} ROWS ONLY");
            }
        }

        if (q.ForClause is not null)
        {
            sb.Append(' ').Append(Unsupported(q.ForClause, "FOR XML / FOR JSON is not translated; use xmlagg / json_agg", string.Empty));
        }

        return sb.ToString();
    }

    private string Select(QuerySpecification s, string? selectList)
    {
        var sb = new StringBuilder("SELECT ");
        if (s.UniqueRowFilter == UniqueRowFilter.Distinct)
        {
            sb.Append("DISTINCT ");
        }

        sb.Append(selectList ?? string.Join(", ", s.SelectElements.Select(SelectElement)));
        if (s.FromClause is { } from)
        {
            sb.Append(" FROM ").Append(string.Join(", ", from.TableReferences.Select(TableRef)));
        }

        if (s.WhereClause is { } where)
        {
            sb.Append(" WHERE ").Append(where.SearchCondition is null ? Unsupported(where, "cursor WHERE CURRENT OF is not translated", "true") : Bool(where.SearchCondition));
        }

        if (s.GroupByClause is { } group)
        {
            sb.Append(" GROUP BY ").Append(string.Join(", ", group.GroupingSpecifications.Select(g => g is ExpressionGroupingSpecification e ? Expr(e.Expression) : Unsupported(g, "grouping not translated"))));
            if (group.GroupByOption != GroupByOption.None)
            {
                sb.Append(Note($"GROUP BY ... WITH {group.GroupByOption} is not translated; use ROLLUP(...) or CUBE(...)"));
            }
        }

        if (s.HavingClause is { } having)
        {
            sb.Append(" HAVING ").Append(Bool(having.SearchCondition));
        }

        return sb.ToString();
    }

    private string SelectElement(SelectElement element) => element switch
    {
        SelectScalarExpression e => Expr(e.Expression) + (e.ColumnName is { } alias ? " AS " + ctx.Ident(alias.Value) : string.Empty),
        SelectStarExpression star => star.Qualifier is null ? "*" : Parts(star.Qualifier.Identifiers) + ".*",
        _ => Unsupported(element, "select element not translated"),
    };

    private string OrderBy(OrderByClause order) =>
        "ORDER BY " + string.Join(", ", order.OrderByElements.Select(o => Expr(o.Expression) + (o.SortOrder == SortOrder.Descending ? " DESC" : string.Empty)));

    private string Alias(Identifier? alias) => alias is null ? string.Empty : " AS " + ctx.Ident(alias.Value);

    private string TableRef(TableReference t)
    {
        switch (t)
        {
            case NamedTableReference n:
            {
                var baseName = n.SchemaObject.BaseIdentifier.Value;
                if (n.SchemaObject.SchemaIdentifier is null && scope.TriggerTable is { } table
                    && (baseName.Equals("inserted", StringComparison.OrdinalIgnoreCase) || baseName.Equals("deleted", StringComparison.OrdinalIgnoreCase)))
                {
                    var present = baseName.Equals("inserted", StringComparison.OrdinalIgnoreCase) ? scope.HasInserted : scope.HasDeleted;
                    var alias = n.Alias?.Value ?? baseName.ToLowerInvariant();
                    return present
                        ? baseName.ToLowerInvariant() + (n.Alias is null ? string.Empty : " AS " + ctx.Ident(alias))
                        : $"(SELECT * FROM {table} WHERE false) AS {ctx.Ident(alias)}";
                }

                return ObjectName(n.SchemaObject) + Alias(n.Alias);
            }

            case QualifiedJoin j:
            {
                var kind = j.QualifiedJoinType switch
                {
                    QualifiedJoinType.LeftOuter => "LEFT JOIN",
                    QualifiedJoinType.RightOuter => "RIGHT JOIN",
                    QualifiedJoinType.FullOuter => "FULL JOIN",
                    _ => "JOIN",
                };
                return $"{TableRef(j.FirstTableReference)} {kind} {TableRef(j.SecondTableReference)} ON {Bool(j.SearchCondition)}";
            }

            case UnqualifiedJoin u:
                return u.UnqualifiedJoinType switch
                {
                    UnqualifiedJoinType.CrossApply => $"{TableRef(u.FirstTableReference)} CROSS JOIN LATERAL {TableRef(u.SecondTableReference)}",
                    UnqualifiedJoinType.OuterApply => $"{TableRef(u.FirstTableReference)} LEFT JOIN LATERAL {TableRef(u.SecondTableReference)} ON true",
                    _ => $"{TableRef(u.FirstTableReference)} CROSS JOIN {TableRef(u.SecondTableReference)}",
                };
            case JoinParenthesisTableReference p:
                return "(" + TableRef(p.Join) + ")";
            case QueryDerivedTable d:
                return $"({Query(d.QueryExpression)}){Alias(d.Alias)}{Columns(d.Columns)}";
            case InlineDerivedTable v:
                return $"(VALUES {string.Join(", ", v.RowValues.Select(r => "(" + string.Join(", ", r.ColumnValues.Select(Expr)) + ")"))}){Alias(v.Alias)}{Columns(v.Columns)}";
            case SchemaObjectFunctionTableReference f:
                return $"{ObjectName(f.SchemaObject)}({string.Join(", ", f.Parameters.Select(Expr))}){Alias(f.Alias)}{Columns(f.Columns)}";
            case VariableTableReference v:
                return ctx.Quote(VariableName(v.Variable.Name, "t_")) + Alias(v.Alias);
            case GlobalFunctionTableReference g when g.Name.Value.Equals("STRING_SPLIT", StringComparison.OrdinalIgnoreCase):
                return $"unnest(string_to_array({Expr(g.Parameters[0])}, {Expr(g.Parameters[1])})){(g.Alias is null ? " AS string_split" : Alias(g.Alias))}(value)";
            default:
                return Unsupported(t, "table source not translated", "(SELECT NULL) AS untranslated");
        }
    }

    private string Columns(IList<Identifier>? columns) =>
        columns is null || columns.Count == 0 ? string.Empty : "(" + string.Join(", ", columns.Select(Id)) + ")";

    private string With(WithCtesAndXmlNamespaces? with)
    {
        if (with is null || with.CommonTableExpressions.Count == 0)
        {
            return string.Empty;
        }

        var recursive = with.CommonTableExpressions.Any(c => Regex.IsMatch(Text(c.QueryExpression), $@"\b{Regex.Escape(c.ExpressionName.Value)}\b", RegexOptions.IgnoreCase));
        return "WITH " + (recursive ? "RECURSIVE " : string.Empty)
            + string.Join(", ", with.CommonTableExpressions.Select(c => $"{Id(c.ExpressionName)}{Columns(c.Columns)} AS ({Query(c.QueryExpression)})")) + " ";
    }
}
