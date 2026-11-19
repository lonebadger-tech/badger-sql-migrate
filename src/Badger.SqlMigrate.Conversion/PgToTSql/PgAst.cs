namespace Badger.SqlMigrate.Conversion.PgToTSql;

// A syntax tree for the PostgreSQL that pg_get_viewdef / pg_get_functiondef produce and PL/pgSQL bodies use.
// Anything the parser does not understand becomes a Raw node carrying the original text.

internal abstract record PgNode;

// ------------------------------------------------------------------ expressions

internal abstract record PgExpr : PgNode;

internal enum LiteralKind
{
    Number,
    String,
    Null,
    True,
    False,
    Default,
}

internal sealed record PgLiteral(LiteralKind Kind, string Value) : PgExpr;

/// <summary>A (possibly qualified) name: column, alias.column, schema.table.column, variable, record.field.</summary>
internal sealed record PgName(IReadOnlyList<string> Parts) : PgExpr
{
    public string Last => Parts[^1];
}

internal sealed record PgStar(PgName? Qualifier) : PgExpr;

internal sealed record PgParam(int Index) : PgExpr;

internal sealed record PgWindow(IReadOnlyList<PgExpr> PartitionBy, IReadOnlyList<PgOrder> OrderBy, string? Frame);

internal sealed record PgCall(PgName Function, IReadOnlyList<PgExpr> Args) : PgExpr
{
    public bool Distinct { get; init; }

    public bool StarArg { get; init; }

    /// <summary>ORDER BY inside the call (string_agg(x, ',' ORDER BY y)) or WITHIN GROUP.</summary>
    public IReadOnlyList<PgOrder> OrderBy { get; init; } = [];

    public PgExpr? Filter { get; init; }

    public PgWindow? Over { get; init; }

    /// <summary>Named arguments (name => value), parallel to Args; null for positional.</summary>
    public IReadOnlyList<string?> ArgNames { get; init; } = [];
}

internal sealed record PgUnary(string Op, PgExpr Operand) : PgExpr;

internal sealed record PgBinary(string Op, PgExpr Left, PgExpr Right) : PgExpr;

internal sealed record PgCast(PgExpr Operand, string Type) : PgExpr;

internal sealed record PgCase(PgExpr? Input, IReadOnlyList<(PgExpr When, PgExpr Then)> Whens, PgExpr? Else) : PgExpr;

/// <summary>IS [NOT] NULL / TRUE / FALSE / UNKNOWN.</summary>
internal sealed record PgIs(PgExpr Operand, string What, bool Not) : PgExpr;

internal sealed record PgIsDistinct(PgExpr Left, PgExpr Right, bool Not) : PgExpr;

internal sealed record PgBetween(PgExpr Operand, PgExpr Low, PgExpr High, bool Not) : PgExpr;

internal sealed record PgInList(PgExpr Operand, IReadOnlyList<PgExpr> Values, bool Not) : PgExpr;

internal sealed record PgInQuery(PgExpr Operand, PgQuery Query, bool Not) : PgExpr;

/// <summary>x LIKE / ILIKE / SIMILAR TO / ~ pattern.</summary>
internal sealed record PgLike(PgExpr Operand, string Op, PgExpr Pattern, PgExpr? Escape, bool Not) : PgExpr;

internal sealed record PgExists(PgQuery Query) : PgExpr;

internal sealed record PgSubquery(PgQuery Query) : PgExpr;

/// <summary>x op ANY|ALL (array or subquery).</summary>
internal sealed record PgQuantified(PgExpr Left, string Op, string Quantifier, PgExpr? Array, PgQuery? Query) : PgExpr;

internal sealed record PgArray(IReadOnlyList<PgExpr> Elements) : PgExpr;

internal sealed record PgSubscript(PgExpr Operand, PgExpr Index) : PgExpr;

internal sealed record PgRow(IReadOnlyList<PgExpr> Fields) : PgExpr;

internal sealed record PgExtract(string Field, PgExpr Source) : PgExpr;

internal sealed record PgAtTimeZone(PgExpr Operand, PgExpr Zone) : PgExpr;

internal sealed record PgCollate(PgExpr Operand, string Collation) : PgExpr;

/// <summary>Typed literal: interval '1 day', date '2020-01-01', timestamp '...'.</summary>
internal sealed record PgTypedLiteral(string Type, string Value) : PgExpr;

internal sealed record PgRawExpr(string Text) : PgExpr;

// ------------------------------------------------------------------ queries

internal sealed record PgOrder(PgExpr Expr, bool Descending, string? Nulls);

internal sealed record PgTarget(PgExpr Expr, string? Alias);

internal abstract record PgFrom : PgNode;

internal sealed record PgTableRef(PgName Name, string? Alias, IReadOnlyList<string> ColumnAliases) : PgFrom;

internal sealed record PgSubqueryRef(PgQuery Query, string? Alias, IReadOnlyList<string> ColumnAliases, bool Lateral) : PgFrom;

internal sealed record PgFunctionRef(PgCall Call, string? Alias, IReadOnlyList<string> ColumnAliases, bool Lateral, bool Ordinality) : PgFrom;

internal sealed record PgValuesRef(IReadOnlyList<IReadOnlyList<PgExpr>> Rows, string? Alias, IReadOnlyList<string> ColumnAliases) : PgFrom;

internal sealed record PgJoin(PgFrom Left, PgFrom Right, string Kind, PgExpr? On, IReadOnlyList<string> Using, bool Natural) : PgFrom;

internal abstract record PgQueryBody : PgNode;

internal sealed record PgSelectCore(
    bool Distinct,
    IReadOnlyList<PgExpr>? DistinctOn,
    IReadOnlyList<PgTarget> Targets,
    IReadOnlyList<PgFrom> From,
    PgExpr? Where,
    IReadOnlyList<PgExpr> GroupBy,
    PgExpr? Having) : PgQueryBody
{
    /// <summary>PL/pgSQL SELECT ... INTO targets.</summary>
    public IReadOnlyList<PgExpr> Into { get; init; } = [];

    public bool IntoStrict { get; init; }
}

internal sealed record PgSetOp(string Op, bool All, PgQueryBody Left, PgQueryBody Right) : PgQueryBody;

internal sealed record PgNestedQuery(PgQuery Query) : PgQueryBody;

internal sealed record PgValuesBody(IReadOnlyList<IReadOnlyList<PgExpr>> Rows) : PgQueryBody;

internal sealed record PgCte(string Name, IReadOnlyList<string> Columns, PgStatement Body);

internal sealed record PgQuery(IReadOnlyList<PgCte> With, bool Recursive, PgQueryBody Body, IReadOnlyList<PgOrder> OrderBy, PgExpr? Limit, PgExpr? Offset) : PgNode
{
    public bool LockingClause { get; init; }
}

// ------------------------------------------------------------------ statements

internal abstract record PgStatement : PgNode
{
    /// <summary>The statement's original text (for REVIEW comments).</summary>
    public string Source { get; init; } = string.Empty;
}

internal sealed record PgSelectStatement(PgQuery Query) : PgStatement;

internal sealed record PgInsert(IReadOnlyList<PgCte> With, PgName Table, string? Alias, IReadOnlyList<string> Columns, PgQuery? Rows, bool DefaultValues, IReadOnlyList<PgTarget> Returning, IReadOnlyList<PgExpr> Into) : PgStatement
{
    public string? OnConflict { get; init; }
}

internal sealed record PgUpdate(IReadOnlyList<PgCte> With, PgName Table, string? Alias, IReadOnlyList<(string Column, PgExpr Value)> Sets, IReadOnlyList<PgFrom> From, PgExpr? Where, IReadOnlyList<PgTarget> Returning, IReadOnlyList<PgExpr> Into) : PgStatement;

internal sealed record PgDelete(IReadOnlyList<PgCte> With, PgName Table, string? Alias, IReadOnlyList<PgFrom> Using, PgExpr? Where, IReadOnlyList<PgTarget> Returning, IReadOnlyList<PgExpr> Into) : PgStatement;

internal sealed record PgAssign(PgExpr Target, PgExpr Value) : PgStatement;

internal sealed record PgIf(IReadOnlyList<(PgExpr Condition, IReadOnlyList<PgStatement> Body)> Branches, IReadOnlyList<PgStatement>? Else) : PgStatement;

internal sealed record PgLoop(string? Label, IReadOnlyList<PgStatement> Body) : PgStatement;

internal sealed record PgWhile(string? Label, PgExpr Condition, IReadOnlyList<PgStatement> Body) : PgStatement;

internal sealed record PgForRange(string? Label, string Variable, PgExpr From, PgExpr To, PgExpr? Step, bool Reverse, IReadOnlyList<PgStatement> Body) : PgStatement;

internal sealed record PgForQuery(string? Label, string Variable, PgQuery? Query, PgExpr? Dynamic, IReadOnlyList<PgStatement> Body) : PgStatement;

internal sealed record PgExit(bool Continue, string? Label, PgExpr? When) : PgStatement;

internal sealed record PgReturn(PgExpr? Value) : PgStatement;

internal sealed record PgReturnQuery(PgQuery Query) : PgStatement;

internal sealed record PgReturnNext(PgExpr? Value) : PgStatement;

internal sealed record PgRaise(string Level, PgExpr? Format, IReadOnlyList<PgExpr> Args, IReadOnlyDictionary<string, PgExpr> Using) : PgStatement;

internal sealed record PgPerform(PgQuery Query) : PgStatement;

internal sealed record PgExecute(PgExpr Sql, IReadOnlyList<PgExpr> Into, IReadOnlyList<PgExpr> Using) : PgStatement;

internal sealed record PgGetDiagnostics(IReadOnlyList<(PgExpr Target, string Item)> Items) : PgStatement;

internal sealed record PgCallStatement(PgCall Call) : PgStatement;

internal sealed record PgNull : PgStatement;

internal sealed record PgDeclaration(string Name, string? Type, PgExpr? Default, bool Constant, bool NotNull)
{
    /// <summary>name%TYPE / table%ROWTYPE / record / ALIAS FOR $n: kept so the emitter can resolve or flag it.</summary>
    public string? Special { get; init; }
}

internal sealed record PgHandler(IReadOnlyList<string> Conditions, IReadOnlyList<PgStatement> Body);

internal sealed record PgBlock(string? Label, IReadOnlyList<PgDeclaration> Declarations, IReadOnlyList<PgStatement> Body, IReadOnlyList<PgHandler> Handlers) : PgStatement;

internal sealed record PgRaw(string Why) : PgStatement;
