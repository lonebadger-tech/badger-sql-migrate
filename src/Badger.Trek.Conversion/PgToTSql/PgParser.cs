using System.Globalization;

namespace Badger.Trek.Conversion.PgToTSql;

internal sealed class PgParseException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

/// <summary>
/// Recursive-descent parser for PostgreSQL queries, DML and PL/pgSQL. It covers what views and routines are commonly
/// written with; a statement it cannot parse becomes <see cref="PgRaw"/> (with its text) instead of failing the object.
/// </summary>
internal sealed class PgParser
{
    private readonly string text;
    private readonly List<Token> tokens;
    private int pos;

    /// <param name="sqlite">Read SQLite: [name] and `name` identifiers, ==, a IS b, GLOB, INSERT OR REPLACE.</param>
    public PgParser(string text, bool sqlite = false)
        : this(text, sqlite ? ParseMode.Sqlite : ParseMode.PostgreSql)
    {
    }

    /// <param name="mode">
    /// SQLite: see the other constructor. MySQL: `name` identifiers, backslash escapes, # comments, &lt;=&gt;, || and &amp;&amp;
    /// as OR and AND, !, DIV, MOD, ^ (XOR), REGEXP / RLIKE, x'..' and b'..', INTERVAL n UNIT, LIMIT offset, count,
    /// CONVERT(), GROUP_CONCAT(... SEPARATOR ...) and CREATE ... VIEW as SHOW CREATE VIEW writes it.
    /// </param>
    public PgParser(string text, ParseMode mode)
    {
        this.text = text;
        sqlite = mode == ParseMode.Sqlite;
        mysql = mode == ParseMode.MySql;
        tokens = PgLexer.Tokenize(text, mode);
    }

    private readonly bool sqlite;

    private readonly bool mysql;

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "from", "where", "group", "having", "order", "limit", "offset", "union", "except", "intersect", "into", "on", "using", "join", "inner", "left", "right",
        "full", "cross", "natural", "window", "returning", "set", "values", "fetch", "for", "and", "or", "not", "as", "lateral", "when", "then", "else", "end",
        "loop", "with", "select", "is", "in", "like", "ilike", "between", "similar", "case", "asc", "desc", "nulls", "collate", "do", "elsif", "elseif", "exception",
        "begin", "declare", "if", "strict", "outer", "default", "all", "distinct", "escape", "within", "filter", "over", "isnull", "notnull", "reverse",
    };

    // ------------------------------------------------------------------ token helpers

    private Token Peek(int ahead = 0) => tokens[Math.Min(pos + ahead, tokens.Count - 1)];

    private Token Next() => tokens[Math.Min(pos++, tokens.Count - 1)];

    private bool AtEnd => Peek().Kind == TokenKind.End;

    private bool IsWord(string word, int ahead = 0) => Peek(ahead).Is(word);

    private bool IsPunct(string p, int ahead = 0) => Peek(ahead).IsPunct(p);

    private bool AcceptWord(string word)
    {
        if (IsWord(word))
        {
            pos++;
            return true;
        }

        return false;
    }

    private bool AcceptWords(params string[] words)
    {
        for (var i = 0; i < words.Length; i++)
        {
            if (!IsWord(words[i], i))
            {
                return false;
            }
        }

        pos += words.Length;
        return true;
    }

    private bool AcceptPunct(string p)
    {
        if (IsPunct(p))
        {
            pos++;
            return true;
        }

        return false;
    }

    private void ExpectWord(string word)
    {
        if (!AcceptWord(word))
        {
            throw Error($"expected {word.ToUpperInvariant()}");
        }
    }

    private void ExpectPunct(string p)
    {
        if (!AcceptPunct(p))
        {
            throw Error($"expected '{p}'");
        }
    }

    private PgParseException Error(string message) => new($"{message} near '{Peek().Text}'", Peek().Start);

    private string Identifier()
    {
        var t = Peek();
        if (t.Kind == TokenKind.QuotedIdent)
        {
            pos++;
            return t.Value;
        }

        if (t.Kind == TokenKind.Word)
        {
            pos++;
            return t.Value;
        }

        throw Error("expected a name");
    }

    private bool IsIdentifierStart => Peek().Kind == TokenKind.QuotedIdent || (Peek().Kind == TokenKind.Word && !Reserved.Contains(Peek().Value));

    private PgName QualifiedName()
    {
        var parts = new List<string> { Identifier() };
        while (IsPunct(".") && (Peek(1).Kind is TokenKind.Word or TokenKind.QuotedIdent))
        {
            pos++;
            parts.Add(Identifier());
        }

        return new PgName(parts);
    }

    private string SourceFrom(int startToken) =>
        startToken >= tokens.Count ? string.Empty : text[tokens[startToken].Start..tokens[Math.Max(startToken, pos - 1)].End];

    // ------------------------------------------------------------------ routine bodies

    /// <summary>A PL/pgSQL body (DECLARE ... BEGIN ... END).</summary>
    public PgBlock PlpgsqlBody()
    {
        var block = Block(null);
        AcceptPunct(";");
        return block;
    }

    /// <summary>A LANGUAGE sql body: statements separated by semicolons.</summary>
    public List<PgStatement> SqlBody()
    {
        var list = new List<PgStatement>();
        if (AcceptWords("begin", "atomic"))
        {
            while (!AtEnd && !IsWord("end"))
            {
                list.Add(SafeStatement());
            }

            return list;
        }

        while (!AtEnd)
        {
            if (AcceptPunct(";"))
            {
                continue;
            }

            list.Add(SafeStatement());
        }

        return list;
    }

    /// <summary>A whole SELECT (for views).</summary>
    public PgQuery QueryOnly()
    {
        var q = Query();
        AcceptPunct(";");
        if (!AtEnd)
        {
            throw Error("unexpected text after the query");
        }

        return q;
    }

    public PgExpr ExpressionOnly()
    {
        var e = Expr();
        if (!AtEnd)
        {
            throw Error("unexpected text after the expression");
        }

        return e;
    }

    private PgBlock Block(string? label)
    {
        var declarations = new List<PgDeclaration>();
        if (AcceptWord("declare"))
        {
            while (!AtEnd && !IsWord("begin"))
            {
                declarations.Add(Declaration());
            }
        }

        ExpectWord("begin");
        var body = Statements("end", "exception");
        var handlers = new List<PgHandler>();
        if (AcceptWord("exception"))
        {
            while (AcceptWord("when"))
            {
                var conditions = new List<string>();
                do
                {
                    if (AcceptWord("sqlstate"))
                    {
                        conditions.Add("sqlstate " + Next().Value);
                    }
                    else
                    {
                        conditions.Add(Identifier());
                    }
                }
                while (AcceptWord("or"));

                ExpectWord("then");
                handlers.Add(new PgHandler(conditions, Statements("when", "end")));
            }
        }

        ExpectWord("end");
        if (Peek().Kind == TokenKind.Word && !IsPunct(";"))
        {
            AcceptWord(label ?? "\0");
        }

        return new PgBlock(label, declarations, body, handlers);
    }

    private PgDeclaration Declaration()
    {
        var start = pos;
        try
        {
            if (IsPunct("<<"))
            {
                throw Error("label in DECLARE");
            }

            var name = Identifier();
            if (AcceptWord("alias"))
            {
                ExpectWord("for");
                var target = Next().Text;
                ExpectPunct(";");
                return new PgDeclaration(name, null, null, false, false) { Special = "alias " + target };
            }

            var constant = AcceptWord("constant");
            if (IsWord("cursor") || (IsWord("no") && IsWord("scroll", 1)) || IsWord("scroll"))
            {
                SkipTo(";");
                return new PgDeclaration(name, null, null, false, false) { Special = "cursor" };
            }

            var typeStart = pos;
            var type = TypeName();
            string? special = null;
            if (IsPunct("%") && (IsWord("type", 1) || IsWord("rowtype", 1)))
            {
                pos++;
                special = $"{type}%{Next().Value}";
            }
            else if (type is "record")
            {
                special = "record";
            }

            if (AcceptWord("collate"))
            {
                Identifier();
            }

            var notNull = AcceptWords("not", "null");
            PgExpr? value = null;
            if (AcceptPunct(":=") || AcceptPunct("=") || AcceptWord("default"))
            {
                value = Expr();
            }

            ExpectPunct(";");
            _ = typeStart;
            return new PgDeclaration(name, type, value, constant, notNull) { Special = special };
        }
        catch (PgParseException)
        {
            pos = start;
            var name = Peek().Value;
            SkipTo(";");
            return new PgDeclaration(name, null, null, false, false) { Special = "unparsed: " + SourceFrom(start) };
        }
    }

    private void SkipTo(string punct)
    {
        var depth = 0;
        while (!AtEnd)
        {
            var t = Next();
            if (t.IsPunct("("))
            {
                depth++;
            }
            else if (t.IsPunct(")"))
            {
                depth--;
            }
            else if (depth <= 0 && t.IsPunct(punct))
            {
                return;
            }
        }
    }

    private List<PgStatement> Statements(params string[] terminators)
    {
        var list = new List<PgStatement>();
        while (!AtEnd && !terminators.Any(t => IsWord(t)) && !(IsWord("else") || IsWord("elsif") || IsWord("elseif")))
        {
            list.Add(SafeStatement());
        }

        return list;
    }

    private List<PgStatement> StatementsUntil(Func<bool> stop)
    {
        var list = new List<PgStatement>();
        while (!AtEnd && !stop())
        {
            list.Add(SafeStatement());
        }

        return list;
    }

    private PgStatement SafeStatement()
    {
        var start = pos;
        try
        {
            var s = Statement();
            return s with { Source = SourceFrom(start) };
        }
        catch (PgParseException ex)
        {
            pos = start;
            SkipStatement();
            return new PgRaw(ex.Message) { Source = SourceFrom(start) };
        }
    }

    /// <summary>Skips one statement, including a nested block or control structure.</summary>
    private void SkipStatement()
    {
        var depth = 0;
        while (!AtEnd)
        {
            var t = Next();
            if (t.Is("begin") || t.Is("loop") || (t.Is("if") && !Peek(-2).Is("end")) || (t.Is("case") && !Peek(-2).Is("end")))
            {
                depth++;
            }
            else if (t.Is("end"))
            {
                depth--;
                if (Peek().Is("if") || Peek().Is("loop") || Peek().Is("case"))
                {
                    pos++;
                }
            }
            else if (t.IsPunct(";") && depth <= 0)
            {
                return;
            }
        }
    }

    private PgStatement Statement()
    {
        string? label = null;
        if (IsPunct("<<"))
        {
            pos++;
            label = Identifier();
            ExpectPunct(">>");
        }

        var t = Peek();
        if (t.Kind == TokenKind.Word)
        {
            switch (t.Value)
            {
                case "declare":
                case "begin":
                    var block = Block(label);
                    ExpectPunct(";");
                    return block;
                case "if":
                    return If();
                case "loop":
                    pos++;
                    var loopBody = Statements("end");
                    ExpectWord("end");
                    ExpectWord("loop");
                    AcceptLabel();
                    ExpectPunct(";");
                    return new PgLoop(label, loopBody);
                case "while":
                {
                    pos++;
                    var condition = Expr();
                    ExpectWord("loop");
                    var body = Statements("end");
                    ExpectWord("end");
                    ExpectWord("loop");
                    AcceptLabel();
                    ExpectPunct(";");
                    return new PgWhile(label, condition, body);
                }

                case "for":
                    return For(label);
                case "exit":
                case "continue":
                {
                    pos++;
                    string? target = Peek().Kind == TokenKind.Word && !IsWord("when") ? Identifier() : null;
                    PgExpr? when = AcceptWord("when") ? Expr() : null;
                    ExpectPunct(";");
                    return new PgExit(t.Value == "continue", target, when);
                }

                case "return":
                    return Return();
                case "raise":
                    return Raise();
                case "perform":
                {
                    pos++;
                    var q = QueryAfterSelectKeyword();
                    ExpectPunct(";");
                    return new PgPerform(q);
                }

                case "execute":
                {
                    pos++;
                    var sql = Expr();
                    var into = new List<PgExpr>();
                    var usingArgs = new List<PgExpr>();
                    while (true)
                    {
                        if (AcceptWord("into"))
                        {
                            AcceptWord("strict");
                            into.AddRange(ExprList());
                        }
                        else if (AcceptWord("using"))
                        {
                            usingArgs.AddRange(ExprList());
                        }
                        else
                        {
                            break;
                        }
                    }

                    ExpectPunct(";");
                    return new PgExecute(sql, into, usingArgs);
                }

                case "get":
                {
                    pos++;
                    AcceptWord("current");
                    if (AcceptWord("stacked"))
                    {
                        throw Error("GET STACKED DIAGNOSTICS");
                    }

                    ExpectWord("diagnostics");
                    var items = new List<(PgExpr, string)>();
                    do
                    {
                        var target = Primary();
                        if (!AcceptPunct(":="))
                        {
                            ExpectPunct("=");
                        }

                        items.Add((target, Identifier()));
                    }
                    while (AcceptPunct(","));

                    ExpectPunct(";");
                    return new PgGetDiagnostics(items);
                }

                case "call":
                {
                    pos++;
                    var call = Primary() as PgCall ?? throw Error("expected a procedure call");
                    ExpectPunct(";");
                    return new PgCallStatement(call);
                }

                case "null":
                    pos++;
                    ExpectPunct(";");
                    return new PgNull();
                case "select":
                case "with":
                case "values":
                case "insert":
                case "replace" when sqlite:
                case "update":
                case "delete":
                {
                    var s = SqlStatement();
                    AcceptPunct(";");
                    return s;
                }

                case "assert":
                {
                    pos++;
                    var condition = Expr();
                    PgExpr? message = AcceptPunct(",") ? Expr() : null;
                    ExpectPunct(";");
                    return new PgIf([(new PgUnary("not", condition), [new PgRaise("exception", message ?? new PgLiteral(LiteralKind.String, "assertion failed"), [], new Dictionary<string, PgExpr>())])], null);
                }

                case "case":
                case "open":
                case "fetch":
                case "close":
                case "move":
                case "foreach":
                case "commit":
                case "rollback":
                    throw Error($"{t.Value.ToUpperInvariant()} statement");
            }
        }

        // Assignment: target := value (target may be a.b or a[i]).
        var targetExpr = Postfix(PrimaryName());
        if (!AcceptPunct(":="))
        {
            ExpectPunct("=");
        }

        var value = Expr();
        ExpectPunct(";");
        return new PgAssign(targetExpr, value);
    }

    private void AcceptLabel()
    {
        if (Peek().Kind == TokenKind.Word && !IsPunct(";"))
        {
            pos++;
        }
    }

    private PgExpr PrimaryName()
    {
        if (!(Peek().Kind is TokenKind.Word or TokenKind.QuotedIdent))
        {
            throw Error("expected a statement");
        }

        return QualifiedName();
    }

    private PgStatement If()
    {
        ExpectWord("if");
        var branches = new List<(PgExpr, IReadOnlyList<PgStatement>)>();
        var condition = Expr();
        ExpectWord("then");
        branches.Add((condition, Statements("end")));
        List<PgStatement>? elseBody = null;
        while (true)
        {
            if (AcceptWord("elsif") || AcceptWord("elseif"))
            {
                var c = Expr();
                ExpectWord("then");
                branches.Add((c, Statements("end")));
            }
            else if (AcceptWord("else"))
            {
                elseBody = Statements("end");
            }
            else
            {
                break;
            }
        }

        ExpectWord("end");
        ExpectWord("if");
        ExpectPunct(";");
        return new PgIf(branches, elseBody);
    }

    private PgStatement For(string? label)
    {
        ExpectWord("for");
        var variable = Identifier();
        if (AcceptPunct(","))
        {
            throw Error("FOR with several targets");
        }

        ExpectWord("in");
        var reverse = AcceptWord("reverse");
        if (IsWord("select") || IsWord("with") || IsWord("values"))
        {
            var q = Query();
            ExpectWord("loop");
            var body = Statements("end");
            ExpectWord("end");
            ExpectWord("loop");
            AcceptLabel();
            ExpectPunct(";");
            return new PgForQuery(label, variable, q, null, body);
        }

        if (AcceptWord("execute"))
        {
            var sql = Expr();
            if (AcceptWord("using"))
            {
                ExprList();
            }

            ExpectWord("loop");
            var body = Statements("end");
            ExpectWord("end");
            ExpectWord("loop");
            AcceptLabel();
            ExpectPunct(";");
            return new PgForQuery(label, variable, null, sql, body);
        }

        var from = Additive();
        ExpectPunct("..");
        var to = Additive();
        PgExpr? step = AcceptWord("by") ? Additive() : null;
        ExpectWord("loop");
        var rangeBody = Statements("end");
        ExpectWord("end");
        ExpectWord("loop");
        AcceptLabel();
        ExpectPunct(";");
        return new PgForRange(label, variable, from, to, step, reverse, rangeBody);
    }

    private PgStatement Return()
    {
        ExpectWord("return");
        if (AcceptWord("query"))
        {
            if (IsWord("execute"))
            {
                throw Error("RETURN QUERY EXECUTE");
            }

            var q = Query();
            ExpectPunct(";");
            return new PgReturnQuery(q);
        }

        if (AcceptWord("next"))
        {
            var v = IsPunct(";") ? null : Expr();
            ExpectPunct(";");
            return new PgReturnNext(v);
        }

        var value = IsPunct(";") ? null : Expr();
        ExpectPunct(";");
        return new PgReturn(value);
    }

    private PgStatement Raise()
    {
        ExpectWord("raise");
        var level = "exception";
        if (Peek().Kind == TokenKind.Word && Peek().Value is "debug" or "log" or "info" or "notice" or "warning" or "exception")
        {
            level = Next().Value;
        }

        PgExpr? format = null;
        var args = new List<PgExpr>();
        var options = new Dictionary<string, PgExpr>(StringComparer.Ordinal);
        if (Peek().Kind == TokenKind.String)
        {
            format = new PgLiteral(LiteralKind.String, Next().Value);
            while (AcceptPunct(","))
            {
                args.Add(Expr());
            }
        }
        else if (Peek().Kind == TokenKind.Word && !IsWord("using"))
        {
            // RAISE unique_violation / RAISE SQLSTATE '...'
            if (AcceptWord("sqlstate"))
            {
                options["errcode"] = new PgLiteral(LiteralKind.String, Next().Value);
            }
            else
            {
                options["errcode"] = new PgLiteral(LiteralKind.String, Identifier());
            }
        }

        if (AcceptWord("using"))
        {
            do
            {
                var key = Identifier();
                if (!AcceptPunct("="))
                {
                    ExpectPunct(":=");
                }

                options[key] = Expr();
            }
            while (AcceptPunct(","));
        }

        ExpectPunct(";");
        return new PgRaise(level, format, args, options);
    }

    // ------------------------------------------------------------------ SQL statements

    private PgStatement SqlStatement()
    {
        var with = new List<PgCte>();
        var recursive = false;
        if (IsWord("with"))
        {
            (with, recursive) = With();
        }

        if (IsWord("insert") || (sqlite && IsWord("replace") && IsWord("into", 1)))
        {
            return Insert(with);
        }

        if (IsWord("update"))
        {
            return Update(with);
        }

        if (IsWord("delete"))
        {
            return Delete(with);
        }

        var q = QueryAfterWith(with, recursive);
        return new PgSelectStatement(q);
    }

    private (List<PgCte>, bool) With()
    {
        ExpectWord("with");
        var recursive = AcceptWord("recursive");
        var ctes = new List<PgCte>();
        do
        {
            var name = Identifier();
            var columns = IsPunct("(") ? NameList() : [];
            ExpectWord("as");
            AcceptWords("not", "materialized");
            AcceptWord("materialized");
            ExpectPunct("(");
            var start = pos;
            var body = IsWord("insert") || IsWord("update") || IsWord("delete") ? SqlStatement() : new PgSelectStatement(Query());
            ExpectPunct(")");
            ctes.Add(new PgCte(name, columns, body with { Source = SourceFrom(start) }));
        }
        while (AcceptPunct(","));

        return (ctes, recursive);
    }

    private List<string> NameList()
    {
        ExpectPunct("(");
        var names = new List<string>();
        do
        {
            names.Add(Identifier());
        }
        while (AcceptPunct(","));

        ExpectPunct(")");
        return names;
    }

    private PgStatement Insert(List<PgCte> with)
    {
        string? orAction = null;
        if (AcceptWord("replace"))
        {
            orAction = "OR REPLACE";
        }
        else
        {
            ExpectWord("insert");
            if (AcceptWord("or"))
            {
                orAction = "OR " + Next().Value.ToUpperInvariant();
            }
        }

        ExpectWord("into");
        var table = QualifiedName();
        string? alias = AcceptWord("as") ? Identifier() : null;
        var columns = IsPunct("(") && !(IsWord("select", 1) || IsWord("with", 1) || IsWord("values", 1)) ? NameList() : [];
        AcceptWords("overriding", "system", "value");
        AcceptWords("overriding", "user", "value");
        PgQuery? source = null;
        var defaults = false;
        if (AcceptWords("default", "values"))
        {
            defaults = true;
        }
        else
        {
            source = Query();
        }

        string? onConflict = null;
        if (IsWord("on") && IsWord("conflict", 1))
        {
            var start = pos;
            while (!AtEnd && !IsPunct(";") && !IsWord("returning"))
            {
                Next();
            }

            onConflict = SourceFrom(start);
        }

        var (returning, into) = Returning();
        return new PgInsert(with, table, alias, columns, source, defaults, returning, into) { OnConflict = onConflict ?? orAction };
    }

    private (List<PgTarget>, List<PgExpr>) Returning()
    {
        var returning = new List<PgTarget>();
        var into = new List<PgExpr>();
        if (AcceptWord("returning"))
        {
            returning.AddRange(Targets());
            if (AcceptWord("into"))
            {
                AcceptWord("strict");
                into.AddRange(ExprList());
            }
        }

        return (returning, into);
    }

    private PgStatement Update(List<PgCte> with)
    {
        ExpectWord("update");
        if (sqlite && AcceptWord("or"))
        {
            Next();
        }

        AcceptWord("only");
        var table = QualifiedName();
        string? alias = AcceptWord("as") ? Identifier() : IsIdentifierStart ? Identifier() : null;
        ExpectWord("set");
        var sets = new List<(string, PgExpr)>();
        do
        {
            if (IsPunct("("))
            {
                var names = NameList();
                ExpectPunct("=");
                ExpectPunct("(");
                var values = ExprList();
                ExpectPunct(")");
                sets.AddRange(names.Zip(values, (n, v) => (n, v)));
                continue;
            }

            var column = Identifier();
            if (AcceptPunct("."))
            {
                column = Identifier();
            }

            ExpectPunct("=");
            sets.Add((column, Expr()));
        }
        while (AcceptPunct(","));

        var from = AcceptWord("from") ? FromList() : [];
        var where = AcceptWord("where") ? Expr() : null;
        var (returning, into) = Returning();
        return new PgUpdate(with, table, alias, sets, from, where, returning, into);
    }

    private PgStatement Delete(List<PgCte> with)
    {
        ExpectWord("delete");
        ExpectWord("from");
        AcceptWord("only");
        var table = QualifiedName();
        string? alias = AcceptWord("as") ? Identifier() : IsIdentifierStart ? Identifier() : null;
        var usingList = AcceptWord("using") ? FromList() : [];
        var where = AcceptWord("where") ? Expr() : null;
        var (returning, into) = Returning();
        return new PgDelete(with, table, alias, usingList, where, returning, into);
    }

    // ------------------------------------------------------------------ queries

    private PgQuery Query()
    {
        var with = new List<PgCte>();
        var recursive = false;
        if (IsWord("with"))
        {
            (with, recursive) = With();
        }

        return QueryAfterWith(with, recursive);
    }

    /// <summary>PERFORM: the query without its SELECT keyword.</summary>
    private PgQuery QueryAfterSelectKeyword()
    {
        var core = SelectCore(selectKeywordConsumed: true);
        return Tail(new PgQuery([], false, core, [], null, null));
    }

    private PgQuery QueryAfterWith(List<PgCte> with, bool recursive)
    {
        var body = SetExpression();
        return Tail(new PgQuery(with, recursive, body, [], null, null));
    }

    private PgQuery Tail(PgQuery q)
    {
        var order = new List<PgOrder>();
        if (AcceptWords("order", "by"))
        {
            order.AddRange(OrderList());
        }

        PgExpr? limit = null;
        PgExpr? offset = null;
        var locking = false;
        while (true)
        {
            if (AcceptWord("limit"))
            {
                limit = AcceptWord("all") ? null : Expr();
                if (mysql && AcceptPunct(","))
                {
                    // MySQL: LIMIT offset, count.
                    offset = limit;
                    limit = Expr();
                }
            }
            else if (AcceptWord("offset"))
            {
                offset = Expr();
                _ = AcceptWord("rows") || AcceptWord("row");
            }
            else if (AcceptWord("fetch"))
            {
                _ = AcceptWord("first") || AcceptWord("next");
                limit = IsWord("row") || IsWord("rows") ? new PgLiteral(LiteralKind.Number, "1") : Expr();
                _ = AcceptWord("rows") || AcceptWord("row");
                if (!AcceptWord("only"))
                {
                    AcceptWords("with", "ties");
                }
            }
            else if (IsWord("for") && (IsWord("update", 1) || IsWord("share", 1) || IsWord("no", 1) || IsWord("key", 1)))
            {
                locking = true;
                while (!AtEnd && !IsPunct(";") && !IsPunct(")") && !IsWord("loop"))
                {
                    Next();
                }
            }
            else
            {
                break;
            }
        }

        // PL/pgSQL allows INTO after the whole query.
        if (q.Body is PgSelectCore core && IsWord("into"))
        {
            pos++;
            var strict = AcceptWord("strict");
            q = q with { Body = core with { Into = ExprList(), IntoStrict = strict } };
        }

        return q with { OrderBy = order, Limit = limit, Offset = offset, LockingClause = locking };
    }

    private PgQueryBody SetExpression()
    {
        var left = SetTerm();
        while (IsWord("union") || IsWord("except") || IsWord("intersect"))
        {
            var op = Next().Value;
            var all = AcceptWord("all");
            AcceptWord("distinct");
            var right = SetTerm();
            left = new PgSetOp(op, all, left, right);
        }

        return left;
    }

    private PgQueryBody SetTerm()
    {
        if (IsPunct("("))
        {
            pos++;
            var inner = Query();
            ExpectPunct(")");
            return new PgNestedQuery(inner);
        }

        if (AcceptWord("values"))
        {
            return new PgValuesBody(ValuesRows());
        }

        return SelectCore(selectKeywordConsumed: false);
    }

    private List<IReadOnlyList<PgExpr>> ValuesRows()
    {
        var rows = new List<IReadOnlyList<PgExpr>>();
        do
        {
            ExpectPunct("(");
            rows.Add(ExprList());
            ExpectPunct(")");
        }
        while (AcceptPunct(","));

        return rows;
    }

    private PgSelectCore SelectCore(bool selectKeywordConsumed)
    {
        if (!selectKeywordConsumed)
        {
            ExpectWord("select");
        }

        var distinct = false;
        List<PgExpr>? distinctOn = null;
        if (AcceptWord("distinct"))
        {
            distinct = true;
            if (AcceptWord("on"))
            {
                ExpectPunct("(");
                distinctOn = ExprList();
                ExpectPunct(")");
            }
        }
        else
        {
            AcceptWord("all");
        }

        var targets = IsWord("from") || IsPunct(";") || AtEnd ? new List<PgTarget>() : Targets();
        var into = new List<PgExpr>();
        var strict = false;
        if (AcceptWord("into"))
        {
            strict = AcceptWord("strict");
            into = ExprList();
        }

        var from = AcceptWord("from") ? FromList() : [];
        var where = AcceptWord("where") ? Expr() : null;
        var group = new List<PgExpr>();
        if (AcceptWords("group", "by"))
        {
            group = ExprList();
        }

        var having = AcceptWord("having") ? Expr() : null;
        if (IsWord("window"))
        {
            throw Error("WINDOW clause");
        }

        return new PgSelectCore(distinct, distinctOn, targets, from, where, group, having) { Into = into, IntoStrict = strict };
    }

    private List<PgTarget> Targets()
    {
        var list = new List<PgTarget>();
        do
        {
            if (IsPunct("*"))
            {
                pos++;
                list.Add(new PgTarget(new PgStar(null), null));
                continue;
            }

            var e = Expr();
            string? alias = null;
            if (AcceptWord("as"))
            {
                alias = Identifier();
            }
            else if (IsIdentifierStart)
            {
                alias = Identifier();
            }

            list.Add(new PgTarget(e, alias));
        }
        while (AcceptPunct(","));

        return list;
    }

    private List<PgOrder> OrderList()
    {
        var list = new List<PgOrder>();
        do
        {
            var e = Expr();
            var desc = false;
            if (AcceptWord("desc"))
            {
                desc = true;
            }
            else
            {
                AcceptWord("asc");
            }

            if (AcceptWord("using"))
            {
                desc = Next().Text == ">";
            }

            string? nulls = null;
            if (AcceptWord("nulls"))
            {
                nulls = Next().Value;
            }

            list.Add(new PgOrder(e, desc, nulls));
        }
        while (AcceptPunct(","));

        return list;
    }

    private List<PgFrom> FromList()
    {
        var list = new List<PgFrom>();
        do
        {
            list.Add(FromItemWithJoins());
        }
        while (AcceptPunct(","));

        return list;
    }

    private PgFrom FromItemWithJoins()
    {
        var left = FromItem();
        while (true)
        {
            var natural = AcceptWord("natural");
            string? kind = null;
            if (AcceptWord("cross"))
            {
                kind = "cross";
            }
            else if (AcceptWord("inner"))
            {
                kind = "inner";
            }
            else if (AcceptWord("left"))
            {
                AcceptWord("outer");
                kind = "left";
            }
            else if (AcceptWord("right"))
            {
                AcceptWord("outer");
                kind = "right";
            }
            else if (AcceptWord("full"))
            {
                AcceptWord("outer");
                kind = "full";
            }
            else if (IsWord("join"))
            {
                kind = "inner";
            }

            if (kind is null)
            {
                if (natural)
                {
                    throw Error("NATURAL without JOIN");
                }

                return left;
            }

            ExpectWord("join");
            var right = FromItem();
            PgExpr? on = null;
            var usingColumns = new List<string>();
            if (kind != "cross" && !natural)
            {
                if (AcceptWord("on"))
                {
                    on = Expr();
                }
                else if (AcceptWord("using"))
                {
                    usingColumns = NameList();
                }
            }

            left = new PgJoin(left, right, kind, on, usingColumns, natural);
        }
    }

    private (string? Alias, List<string> Columns) AliasClause()
    {
        string? alias = null;
        if (AcceptWord("as"))
        {
            alias = Identifier();
        }
        else if (IsIdentifierStart)
        {
            alias = Identifier();
        }

        var columns = alias is not null && IsPunct("(") ? NameList() : [];
        return (alias, columns);
    }

    private PgFrom FromItem()
    {
        var lateral = AcceptWord("lateral");
        if (IsPunct("("))
        {
            pos++;
            if (IsWord("select") || IsWord("with") || IsWord("values"))
            {
                var q = Query();
                ExpectPunct(")");
                var (alias, columns) = AliasClause();
                return new PgSubqueryRef(q, alias, columns, lateral);
            }

            var inner = FromItemWithJoins();
            ExpectPunct(")");
            return inner;
        }

        if (AcceptWord("values"))
        {
            var rows = ValuesRows();
            var (alias, columns) = AliasClause();
            return new PgValuesRef(rows, alias, columns);
        }

        AcceptWord("only");
        var name = QualifiedName();
        if (IsPunct("("))
        {
            var call = (PgCall)CallAfterName(name);
            var ordinality = AcceptWords("with", "ordinality");
            var (alias, columns) = AliasClause();
            return new PgFunctionRef(call, alias, columns, lateral, ordinality);
        }

        AcceptPunct("*");
        var (a, c) = AliasClause();
        return new PgTableRef(name, a, c);
    }

    // ------------------------------------------------------------------ expressions

    private List<PgExpr> ExprList()
    {
        var list = new List<PgExpr> { Expr() };
        while (AcceptPunct(","))
        {
            list.Add(Expr());
        }

        return list;
    }

    public PgExpr Expr() => Or();

    private PgExpr Or()
    {
        var left = And();
        while (AcceptWord("or") || (mysql && AcceptPunct("||")))
        {
            left = new PgBinary("or", left, And());
        }

        return left;
    }

    private PgExpr And()
    {
        var left = Not();
        while (AcceptWord("and") || (mysql && AcceptPunct("&&")))
        {
            left = new PgBinary("and", left, Not());
        }

        return left;
    }

    private PgExpr Not() => AcceptWord("not") ? new PgUnary("not", Not()) : IsTest();

    private PgExpr IsTest()
    {
        var e = Comparison();
        while (true)
        {
            if (AcceptWord("isnull"))
            {
                e = new PgIs(e, "null", false);
            }
            else if (AcceptWord("notnull"))
            {
                e = new PgIs(e, "null", true);
            }
            else if (AcceptWord("is"))
            {
                var not = AcceptWord("not");
                if (AcceptWords("distinct", "from"))
                {
                    e = new PgIsDistinct(e, Comparison(), not);
                }
                else
                {
                    if (sqlite && !(IsWord("null") || IsWord("true") || IsWord("false") || IsWord("unknown")))
                    {
                        // SQLite: a IS b means IS NOT DISTINCT FROM.
                        e = new PgIsDistinct(e, Comparison(), !not);
                        continue;
                    }

                    var what = Next().Value;
                    if (what is not ("null" or "true" or "false" or "unknown"))
                    {
                        throw Error($"IS {what.ToUpperInvariant()}");
                    }

                    e = new PgIs(e, what, not);
                }
            }
            else
            {
                return e;
            }
        }
    }

    private static readonly HashSet<string> ComparisonOps = new(StringComparer.Ordinal) { "=", "==", "<>", "!=", "<", ">", "<=", ">=" };

    private PgExpr Comparison()
    {
        var left = Pattern();
        while (Peek().Kind == TokenKind.Operator && (ComparisonOps.Contains(Peek().Text) || (mysql && Peek().Text == "<=>")))
        {
            var op = Next().Text;
            if (op == "<=>")
            {
                // MySQL's null-safe equality.
                left = new PgIsDistinct(left, Pattern(), true);
                continue;
            }

            if (op == "!=")
            {
                op = "<>";
            }
            else if (op == "==")
            {
                op = "=";
            }

            if (IsWord("any") || IsWord("all") || IsWord("some"))
            {
                var quantifier = Next().Value == "all" ? "all" : "any";
                ExpectPunct("(");
                if (IsWord("select") || IsWord("with"))
                {
                    var q = Query();
                    ExpectPunct(")");
                    left = new PgQuantified(left, op, quantifier, null, q);
                }
                else
                {
                    var array = Expr();
                    ExpectPunct(")");
                    left = new PgQuantified(left, op, quantifier, array, null);
                }

                continue;
            }

            left = new PgBinary(op, left, Pattern());
        }

        return left;
    }

    private PgExpr Pattern()
    {
        var left = Other();
        while (true)
        {
            var save = pos;
            var not = AcceptWord("not");
            if (AcceptWord("like") || AcceptWord("ilike"))
            {
                var op = Peek(-1).Value;
                var pattern = Other();
                var escape = AcceptWord("escape") ? Other() : null;
                left = new PgLike(left, op, pattern, escape, not);
            }
            else if (sqlite && (AcceptWord("glob") || AcceptWord("regexp")))
            {
                var op = Peek(-1).Value;
                left = new PgLike(left, op, Other(), null, not);
            }
            else if (mysql && (AcceptWord("regexp") || AcceptWord("rlike")))
            {
                left = new PgLike(left, "regexp", Other(), null, not);
            }
            else if (AcceptWords("similar", "to"))
            {
                var pattern = Other();
                var escape = AcceptWord("escape") ? Other() : null;
                left = new PgLike(left, "similar", pattern, escape, not);
            }
            else if (AcceptWord("between"))
            {
                AcceptWord("symmetric");
                var low = Other();
                ExpectWord("and");
                left = new PgBetween(left, low, Other(), not);
            }
            else if (AcceptWord("in"))
            {
                ExpectPunct("(");
                if (IsWord("select") || IsWord("with") || IsWord("values"))
                {
                    var q = Query();
                    ExpectPunct(")");
                    left = new PgInQuery(left, q, not);
                }
                else
                {
                    var values = ExprList();
                    ExpectPunct(")");
                    left = new PgInList(left, values, not);
                }
            }
            else
            {
                pos = save;
                return left;
            }
        }
    }

    private static readonly HashSet<string> OtherOps = new(StringComparer.Ordinal)
    {
        "||", "->", "->>", "#>", "#>>", "@>", "<@", "~", "~*", "!~", "!~*", "~~", "~~*", "!~~", "!~~*", "&&", "<<", ">>", "?", "?|", "?&", "@@", "&", "|", "#",
    };

    private PgExpr Other()
    {
        var left = Additive();
        while (Peek().Kind == TokenKind.Operator && OtherOps.Contains(Peek().Text) && !(mysql && Peek().Text is "||" or "&&"))
        {
            var op = Next().Text;
            var right = Additive();
            left = op switch
            {
                "~~" => new PgLike(left, "like", right, null, false),
                "!~~" => new PgLike(left, "like", right, null, true),
                "~~*" => new PgLike(left, "ilike", right, null, false),
                "!~~*" => new PgLike(left, "ilike", right, null, true),
                "~" or "~*" => new PgLike(left, op, right, null, false),
                "!~" => new PgLike(left, "~", right, null, true),
                "!~*" => new PgLike(left, "~*", right, null, true),
                _ => new PgBinary(op, left, right),
            };
        }

        return left;
    }

    private PgExpr Additive()
    {
        var left = Multiplicative();
        while (IsPunct("+") || IsPunct("-"))
        {
            var op = Next().Text;
            left = new PgBinary(op, left, Multiplicative());
        }

        return left;
    }

    private PgExpr Multiplicative()
    {
        var left = Power();
        while (IsPunct("*") || IsPunct("/") || IsPunct("%") || (mysql && (IsWord("div") || IsWord("mod"))))
        {
            var op = Next().Value switch { "div" => "div", "mod" => "%", var o => o };
            left = new PgBinary(op, left, Power());
        }

        return left;
    }

    private PgExpr Power()
    {
        var left = Unary();
        while (IsPunct("^"))
        {
            pos++;

            // MySQL's ^ is bitwise XOR, PostgreSQL's #.
            left = new PgBinary(mysql ? "#" : "^", left, Unary());
        }

        return left;
    }

    private PgExpr Unary()
    {
        if (mysql && IsPunct("!"))
        {
            pos++;
            return new PgUnary("not", Unary());
        }

        if (IsPunct("-") || IsPunct("+") || IsPunct("~"))
        {
            var op = Next().Text;
            var operand = Unary();
            return op == "-" && operand is PgLiteral { Kind: LiteralKind.Number } n ? new PgLiteral(LiteralKind.Number, "-" + n.Value) : new PgUnary(op, operand);
        }

        return Postfix(Primary());
    }

    private PgExpr Postfix(PgExpr e)
    {
        while (true)
        {
            if (AcceptPunct("::"))
            {
                e = new PgCast(e, TypeName());
            }
            else if (IsPunct("["))
            {
                pos++;
                var index = Expr();
                if (AcceptPunct(":"))
                {
                    Expr();
                    ExpectPunct("]");
                    throw Error("array slice");
                }

                ExpectPunct("]");
                e = new PgSubscript(e, index);
            }
            else if (IsPunct(".") && e is PgSubscript or PgCall && Peek(1).Kind is TokenKind.Word or TokenKind.QuotedIdent)
            {
                throw Error("field selection from an expression");
            }
            else if (AcceptWords("at", "time", "zone"))
            {
                e = new PgAtTimeZone(e, Unary());
            }
            else if (AcceptWord("collate"))
            {
                e = new PgCollate(e, string.Join('.', QualifiedName().Parts));
            }
            else
            {
                return e;
            }
        }
    }

    /// <summary>A type name as written: integer, character varying(20), timestamp(3) with time zone, public.mood, text[].</summary>
    private string TypeName()
    {
        var parts = new List<string>();
        var first = Peek();
        if (first.Kind == TokenKind.QuotedIdent)
        {
            parts.Add(first.Text);
            pos++;
        }
        else
        {
            parts.Add(Identifier());
        }

        // Multi-word built-in names.
        var head = parts[0];
        if (head is "double" && AcceptWord("precision"))
        {
            parts[0] = "double precision";
        }
        else if (head is "character" or "char" or "bit" && AcceptWord("varying"))
        {
            parts[0] = head + " varying";
        }
        else if (mysql && head is "signed" or "unsigned")
        {
            // MySQL: CAST(x AS SIGNED INTEGER).
            _ = AcceptWord("integer") || AcceptWord("int");
        }

        while (IsPunct(".") && Peek(1).Kind is TokenKind.Word or TokenKind.QuotedIdent)
        {
            pos++;
            parts.Add(Peek().Kind == TokenKind.QuotedIdent ? Next().Text : Identifier());
        }

        var type = string.Join('.', parts);
        if (IsPunct("("))
        {
            pos++;
            var args = new List<string>();
            do
            {
                args.Add(Next().Text);
            }
            while (AcceptPunct(","));

            ExpectPunct(")");
            type += "(" + string.Join(",", args) + ")";
        }

        if (mysql && (AcceptWord("charset") || AcceptWords("character", "set")))
        {
            // MySQL: CAST(x AS CHAR CHARSET utf8mb4); the character set does not carry over.
            Identifier();
        }

        if (AcceptWords("with", "time", "zone"))
        {
            type += " with time zone";
        }
        else if (AcceptWords("without", "time", "zone"))
        {
            type += " without time zone";
        }

        while (IsPunct("[") && IsPunct("]", 1))
        {
            pos += 2;
            type += "[]";
        }

        return type;
    }

    private static readonly HashSet<string> TypedLiteralTypes = new(StringComparer.Ordinal) { "interval", "date", "timestamp", "timestamptz", "time", "timetz", "json", "jsonb", "uuid", "numeric", "boolean" };

    private PgExpr Primary()
    {
        var t = Peek();
        switch (t.Kind)
        {
            case TokenKind.Number:
                pos++;
                return new PgLiteral(LiteralKind.Number, t.Text);
            case TokenKind.String when mysql && t.Text.StartsWith("x'", StringComparison.Ordinal):
                // MySQL x'4142' / 0x4142: a binary string.
                pos++;
                return new PgTypedLiteral("bytea", "\\x" + t.Value.ToLowerInvariant());
            case TokenKind.String:
                pos++;
                return new PgLiteral(LiteralKind.String, t.Value);
            case TokenKind.Param:
                pos++;
                return new PgParam(int.Parse(t.Text[1..], CultureInfo.InvariantCulture));
            case TokenKind.Punct when t.Text == "(":
            {
                pos++;
                if (IsWord("select") || IsWord("with") || IsWord("values"))
                {
                    var q = Query();
                    ExpectPunct(")");
                    return new PgSubquery(q);
                }

                var first = Expr();
                if (AcceptPunct(","))
                {
                    var fields = new List<PgExpr> { first };
                    fields.AddRange(ExprList());
                    ExpectPunct(")");
                    return new PgRow(fields);
                }

                ExpectPunct(")");
                return first is PgName or PgLiteral or PgParam or PgCall ? first : new PgCall(new PgName(["("]), [first]);
            }

            case TokenKind.Operator when t.Text == "*":
                pos++;
                return new PgStar(null);
            case TokenKind.Word:
                return WordPrimary(t);
            case TokenKind.QuotedIdent:
                return NameOrCall();
            default:
                throw Error("unexpected token");
        }
    }

    private PgExpr WordPrimary(Token t)
    {
        switch (t.Value)
        {
            case "null":
                pos++;
                return new PgLiteral(LiteralKind.Null, "null");
            case "true":
                pos++;
                return new PgLiteral(LiteralKind.True, "true");
            case "false":
                pos++;
                return new PgLiteral(LiteralKind.False, "false");
            case "default":
                pos++;
                return new PgLiteral(LiteralKind.Default, "default");
            case "case":
                return Case();
            case "interval" when mysql && MySqlInterval() is { } interval:
                return interval;
            case "convert" when mysql && IsPunct("(", 1):
            {
                // MySQL: CONVERT(x, type) is a cast; CONVERT(x USING charset) only changes the character set.
                pos += 2;
                var e = Expr();
                if (AcceptPunct(","))
                {
                    var type = TypeName();
                    ExpectPunct(")");
                    return new PgCast(e, type);
                }

                ExpectWord("using");
                Identifier();
                ExpectPunct(")");
                return e;
            }

            case "cast":
            {
                pos++;
                ExpectPunct("(");
                var e = Expr();
                ExpectWord("as");
                var type = TypeName();
                ExpectPunct(")");
                return new PgCast(e, type);
            }

            case "exists":
            {
                pos++;
                ExpectPunct("(");
                var q = Query();
                ExpectPunct(")");
                return new PgExists(q);
            }

            case "array":
            {
                pos++;
                if (AcceptPunct("["))
                {
                    var elements = IsPunct("]") ? new List<PgExpr>() : ExprList();
                    ExpectPunct("]");
                    return new PgArray(elements);
                }

                ExpectPunct("(");
                var q = Query();
                ExpectPunct(")");
                return new PgCall(new PgName(["array"]), [new PgSubquery(q)]);
            }

            case "row" when IsPunct("(", 1):
            {
                pos += 2;
                var fields = IsPunct(")") ? new List<PgExpr>() : ExprList();
                ExpectPunct(")");
                return new PgRow(fields);
            }

            case "extract" when IsPunct("(", 1):
            {
                pos += 2;
                var field = Peek().Kind == TokenKind.String ? Next().Value : Next().Value;
                ExpectWord("from");
                var source = Expr();
                ExpectPunct(")");
                return new PgExtract(field.ToLowerInvariant(), source);
            }

            case "position" when IsPunct("(", 1):
            {
                pos += 2;
                var needle = Other();
                ExpectWord("in");
                var haystack = Expr();
                ExpectPunct(")");
                return new PgCall(new PgName(["strpos"]), [haystack, needle]);
            }

            case "substring" when IsPunct("(", 1):
            {
                var save = pos;
                pos += 2;
                var source = Expr();
                if (AcceptWord("from"))
                {
                    var start = Expr();
                    var args = new List<PgExpr> { source, start };
                    if (AcceptWord("for"))
                    {
                        args.Add(Expr());
                    }

                    ExpectPunct(")");
                    return new PgCall(new PgName(["substring"]), args);
                }

                if (AcceptWord("for"))
                {
                    var length = Expr();
                    ExpectPunct(")");
                    return new PgCall(new PgName(["substring"]), [source, new PgLiteral(LiteralKind.Number, "1"), length]);
                }

                pos = save;
                return NameOrCall();
            }

            case "trim" when IsPunct("(", 1):
            {
                pos += 2;
                var side = "both";
                if (IsWord("both") || IsWord("leading") || IsWord("trailing"))
                {
                    side = Next().Value;
                }

                PgExpr? chars = null;
                PgExpr source;
                if (AcceptWord("from"))
                {
                    source = Expr();
                }
                else
                {
                    var first = Expr();
                    if (AcceptWord("from"))
                    {
                        chars = first;
                        source = Expr();
                    }
                    else if (AcceptPunct(","))
                    {
                        source = first;
                        chars = Expr();
                    }
                    else
                    {
                        source = first;
                    }
                }

                ExpectPunct(")");
                var fn = side switch { "leading" => "ltrim", "trailing" => "rtrim", _ => "btrim" };
                return new PgCall(new PgName([fn]), chars is null ? [source] : [source, chars]);
            }

            case "current_date":
            case "current_timestamp":
            case "current_time":
            case "localtimestamp":
            case "localtime":
            case "current_user":
            case "session_user":
            case "current_role":
            case "current_catalog":
            case "current_schema" when !IsPunct("(", 1):
            case "user" when !IsPunct("(", 1):
                pos++;
                if (mysql && IsPunct("(") && IsPunct(")", 1))
                {
                    // MySQL: CURRENT_DATE(), CURRENT_USER().
                    pos += 2;
                }
                else if (IsPunct("(") && t.Value is "current_timestamp" or "localtimestamp" or "current_time" or "localtime")
                {
                    pos++;
                    Next();
                    ExpectPunct(")");
                }

                return new PgCall(new PgName([t.Value]), []) { StarArg = false };
        }

        if (TypedLiteralTypes.Contains(t.Value) && Peek(1).Kind == TokenKind.String)
        {
            pos++;
            return new PgTypedLiteral(t.Value, Next().Value);
        }

        if (t.Value is "timestamp" or "time" && (IsWord("with", 1) || IsWord("without", 1)) && Peek(4).Kind == TokenKind.String)
        {
            var type = TypeName();
            return new PgTypedLiteral(type, Next().Value);
        }

        if (t.Value is "double" && IsWord("precision", 1) && Peek(2).Kind == TokenKind.String)
        {
            pos += 2;
            return new PgCast(new PgLiteral(LiteralKind.String, Next().Value), "double precision");
        }

        return NameOrCall();
    }

    private PgExpr Case()
    {
        ExpectWord("case");
        PgExpr? input = IsWord("when") ? null : Expr();
        var whens = new List<(PgExpr, PgExpr)>();
        while (AcceptWord("when"))
        {
            var w = Expr();
            ExpectWord("then");
            whens.Add((w, Expr()));
        }

        var otherwise = AcceptWord("else") ? Expr() : null;
        ExpectWord("end");
        return new PgCase(input, whens, otherwise);
    }

    private PgExpr NameOrCall()
    {
        var parts = new List<string> { Identifier() };
        while (IsPunct("."))
        {
            if (IsPunct("*", 1))
            {
                pos += 2;
                return new PgStar(new PgName(parts));
            }

            if (Peek(1).Kind is not (TokenKind.Word or TokenKind.QuotedIdent))
            {
                break;
            }

            pos++;
            parts.Add(Identifier());
        }

        var name = new PgName(parts);
        return IsPunct("(") ? CallAfterName(name) : name;
    }

    private PgExpr CallAfterName(PgName name)
    {
        ExpectPunct("(");
        var args = new List<PgExpr>();
        var names = new List<string?>();
        var distinct = false;
        var star = false;
        var order = new List<PgOrder>();
        if (AcceptPunct("*"))
        {
            star = true;
        }
        else if (!IsPunct(")"))
        {
            distinct = AcceptWord("distinct");
            AcceptWord("all");
            AcceptWord("variadic");
            do
            {
                if ((Peek().Kind is TokenKind.Word or TokenKind.QuotedIdent) && (IsPunct("=>", 1) || IsPunct(":=", 1)))
                {
                    names.Add(Identifier());
                    pos++;
                }
                else
                {
                    names.Add(null);
                }

                args.Add(Expr());
            }
            while (AcceptPunct(","));

            if (AcceptWords("order", "by"))
            {
                order = OrderList();
            }

            if (mysql && AcceptWord("separator"))
            {
                // MySQL: GROUP_CONCAT(x ORDER BY y SEPARATOR ', '); kept as a last, named argument.
                names.Add("separator");
                args.Add(Expr());
            }
        }

        ExpectPunct(")");
        if (AcceptWords("within", "group"))
        {
            ExpectPunct("(");
            ExpectWords("order", "by");
            order = OrderList();
            ExpectPunct(")");
        }

        PgExpr? filter = null;
        if (AcceptWord("filter"))
        {
            ExpectPunct("(");
            ExpectWord("where");
            filter = Expr();
            ExpectPunct(")");
        }

        PgWindow? over = null;
        if (AcceptWord("over"))
        {
            if (!IsPunct("("))
            {
                throw Error("named window");
            }

            pos++;
            var partition = new List<PgExpr>();
            var windowOrder = new List<PgOrder>();
            string? frame = null;
            if (AcceptWords("partition", "by"))
            {
                partition = ExprList();
            }

            if (AcceptWords("order", "by"))
            {
                windowOrder = OrderList();
            }

            if (IsWord("rows") || IsWord("range") || IsWord("groups"))
            {
                var start = pos;
                var depth = 0;
                while (!AtEnd && !(depth == 0 && IsPunct(")")))
                {
                    var tk = Next();
                    if (tk.IsPunct("("))
                    {
                        depth++;
                    }
                    else if (tk.IsPunct(")"))
                    {
                        depth--;
                    }
                }

                frame = SourceFrom(start);
            }

            ExpectPunct(")");
            over = new PgWindow(partition, windowOrder, frame);
        }

        return new PgCall(name, args) { Distinct = distinct, StarArg = star, OrderBy = order, Filter = filter, Over = over, ArgNames = names };
    }

    private void ExpectWords(params string[] words)
    {
        foreach (var w in words)
        {
            ExpectWord(w);
        }
    }

    // ------------------------------------------------------------------ SQLite triggers

    public SqliteTriggerDef SqliteTrigger()
    {
        ExpectWord("create");
        _ = AcceptWord("temp") || AcceptWord("temporary");
        ExpectWord("trigger");
        AcceptWords("if", "not", "exists");
        var name = QualifiedName().Last;
        var timing = "BEFORE";
        if (AcceptWords("instead", "of"))
        {
            timing = "INSTEAD OF";
        }
        else if (AcceptWord("after"))
        {
            timing = "AFTER";
        }
        else
        {
            AcceptWord("before");
        }

        var ev = Next().Value.ToUpperInvariant();
        var columns = new List<string>();
        if (ev == "UPDATE" && AcceptWord("of"))
        {
            do
            {
                columns.Add(Identifier());
            }
            while (AcceptPunct(","));
        }

        ExpectWord("on");
        var table = QualifiedName();
        AcceptWords("for", "each", "row");
        PgExpr? when = AcceptWord("when") ? Expr() : null;
        ExpectWord("begin");
        var body = StatementsUntil(() => IsWord("end") && (IsPunct(";", 1) || Peek(1).Kind == TokenKind.End));
        ExpectWord("end");
        return new SqliteTriggerDef(name, timing, ev, columns, table, when, body);
    }

    /// <summary>A SQLite view: CREATE [TEMP] VIEW [IF NOT EXISTS] name [(columns)] AS query.</summary>
    public (string Name, IReadOnlyList<string> Columns, PgQuery Query) SqliteView()
    {
        ExpectWord("create");
        _ = AcceptWord("temp") || AcceptWord("temporary");
        ExpectWord("view");
        AcceptWords("if", "not", "exists");
        var name = QualifiedName().Last;
        var columns = IsPunct("(") ? NameList() : [];
        ExpectWord("as");
        return (name, columns, QueryOnly());
    }

    // ------------------------------------------------------------------ MySQL

    /// <summary>
    /// A MySQL view as SHOW CREATE VIEW writes it: CREATE [OR REPLACE] [ALGORITHM=...] [DEFINER=...] [SQL SECURITY ...]
    /// VIEW name [(columns)] AS query [WITH [CASCADED | LOCAL] CHECK OPTION].
    /// </summary>
    public MySqlViewDef MySqlView()
    {
        ExpectWord("create");
        AcceptWords("or", "replace");
        while (!AtEnd && !IsWord("view"))
        {
            if (AcceptWord("algorithm"))
            {
                ExpectPunct("=");
                Next();
            }
            else if (AcceptWord("definer"))
            {
                ExpectPunct("=");
                while (!AtEnd && !IsWord("sql") && !IsWord("view"))
                {
                    Next();
                }
            }
            else if (AcceptWords("sql", "security"))
            {
                Next();
            }
            else
            {
                throw Error("expected VIEW");
            }
        }

        ExpectWord("view");
        var name = QualifiedName().Last;
        var columns = IsPunct("(") ? NameList() : [];
        ExpectWord("as");
        var query = Query();
        string? check = null;
        if (AcceptWord("with"))
        {
            check = AcceptWord("local") ? "LOCAL" : "CASCADED";
            AcceptWord("cascaded");
            ExpectWords("check", "option");
        }

        AcceptPunct(";");
        if (!AtEnd)
        {
            throw Error("unexpected text after the view");
        }

        return new MySqlViewDef(name, columns, query, check);
    }

    /// <summary>MySQL interval units and the PostgreSQL units of their parts (DAY_HOUR '1 2' is 1 day 2 hours).</summary>
    private static readonly Dictionary<string, string[]> MySqlIntervalUnits = new(StringComparer.Ordinal)
    {
        ["microsecond"] = ["microsecond"],
        ["second"] = ["second"],
        ["minute"] = ["minute"],
        ["hour"] = ["hour"],
        ["day"] = ["day"],
        ["week"] = ["week"],
        ["month"] = ["month"],
        ["quarter"] = ["quarter"],
        ["year"] = ["year"],
        ["second_microsecond"] = ["second", "microsecond"],
        ["minute_microsecond"] = ["minute", "second", "microsecond"],
        ["minute_second"] = ["minute", "second"],
        ["hour_microsecond"] = ["hour", "minute", "second", "microsecond"],
        ["hour_second"] = ["hour", "minute", "second"],
        ["hour_minute"] = ["hour", "minute"],
        ["day_microsecond"] = ["day", "hour", "minute", "second", "microsecond"],
        ["day_second"] = ["day", "hour", "minute", "second"],
        ["day_minute"] = ["day", "hour", "minute"],
        ["day_hour"] = ["day", "hour"],
        ["year_month"] = ["year", "month"],
    };

    /// <summary>
    /// MySQL's INTERVAL expr unit, as a PostgreSQL interval: a literal amount becomes interval '2 day', a computed one
    /// (n) * interval '1 day'. Null (nothing consumed) when no unit follows, e.g. the INTERVAL(n, a, b) function.
    /// </summary>
    private PgExpr? MySqlInterval()
    {
        var save = pos;
        pos++;
        try
        {
            var amount = Additive();
            if (Peek().Kind == TokenKind.Word && MySqlIntervalUnits.TryGetValue(Peek().Value, out var units))
            {
                pos++;
                return IntervalValue(amount, units) ?? throw Error("INTERVAL value");
            }
        }
        catch (PgParseException)
        {
            // Not an interval: read it as a name or a call instead.
        }

        pos = save;
        return null;
    }

    private static PgExpr? IntervalValue(PgExpr amount, string[] units)
    {
        var literal = amount switch
        {
            PgLiteral { Kind: LiteralKind.Number or LiteralKind.String } l => l.Value.Trim(),
            _ => null,
        };
        if (units.Length == 1)
        {
            var quarter = units[0] == "quarter";
            if (literal is not null && decimal.TryParse(literal, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
            {
                return new PgTypedLiteral("interval", quarter ? $"{(n * 3).ToString(CultureInfo.InvariantCulture)} month" : $"{literal} {units[0]}");
            }

            var factor = amount is PgName or PgLiteral or PgParam or PgCall ? amount : new PgCall(new PgName(["("]), [amount]);
            return new PgBinary("*", factor, new PgTypedLiteral("interval", quarter ? "3 month" : $"1 {units[0]}"));
        }

        if (literal is null)
        {
            return null;
        }

        var negative = literal.StartsWith('-');
        var values = System.Text.RegularExpressions.Regex.Matches(literal, @"\d+").Select(m => m.Value).ToList();
        if (values.Count == 0 || values.Count > units.Length)
        {
            return null;
        }

        var parts = values.Select((v, i) => $"{(negative ? "-" : string.Empty)}{v} {units[units.Length - values.Count + i]}");
        return new PgTypedLiteral("interval", string.Join(' ', parts));
    }

    // ------------------------------------------------------------------ CREATE FUNCTION / PROCEDURE headers

    /// <summary>Parses pg_get_functiondef output: name, arguments, RETURNS, LANGUAGE and the body text.</summary>
    public RoutineHeader RoutineHeader()
    {
        ExpectWord("create");
        AcceptWords("or", "replace");
        var procedure = AcceptWord("procedure");
        if (!procedure)
        {
            ExpectWord("function");
        }

        var name = QualifiedName();
        ExpectPunct("(");
        var args = new List<RoutineArg>();
        while (!IsPunct(")"))
        {
            var mode = "in";
            if (IsWord("in") || IsWord("out") || IsWord("inout") || IsWord("variadic"))
            {
                mode = Next().Value;
            }

            string? argName = null;
            var multiWordType = (IsWord("double") && IsWord("precision", 1)) || ((IsWord("character") || IsWord("bit") || IsWord("char")) && IsWord("varying", 1))
                || ((IsWord("timestamp") || IsWord("time")) && (IsWord("with", 1) || IsWord("without", 1)));
            if (!multiWordType && (Peek(1).Kind is TokenKind.Word or TokenKind.QuotedIdent) && !IsWord("default", 1))
            {
                argName = Identifier();
            }

            var type = TypeName();
            PgExpr? value = null;
            if (AcceptWord("default") || AcceptPunct("="))
            {
                value = Expr();
            }

            args.Add(new RoutineArg(mode, argName, type, value));
            if (!AcceptPunct(","))
            {
                break;
            }
        }

        ExpectPunct(")");
        string? returns = null;
        var setOf = false;
        var table = new List<(string, string)>();
        var language = "sql";
        string? body = null;
        while (!AtEnd)
        {
            if (AcceptWord("returns"))
            {
                if (AcceptWord("table"))
                {
                    ExpectPunct("(");
                    do
                    {
                        var column = Identifier();
                        table.Add((column, TypeName()));
                    }
                    while (AcceptPunct(","));

                    ExpectPunct(")");
                }
                else
                {
                    setOf = AcceptWord("setof");
                    returns = TypeName();
                }
            }
            else if (AcceptWord("language"))
            {
                language = Peek().Kind == TokenKind.String ? Next().Value.ToLowerInvariant() : Identifier();
            }
            else if (AcceptWord("as"))
            {
                body = Next().Value;
                if (AcceptPunct(","))
                {
                    Next();
                }
            }
            else if (IsWord("begin") && IsWord("atomic", 1))
            {
                body = text[Peek().Start..];
                break;
            }
            else
            {
                Next();
            }
        }

        return new RoutineHeader(name, procedure, args, returns, table, setOf, language, body ?? string.Empty);
    }
}

/// <summary>A SQLite CREATE TRIGGER: one event, row level, a body of SQL statements.</summary>
/// <param name="CheckOption">CASCADED or LOCAL when the view has WITH CHECK OPTION, else null.</param>
internal sealed record MySqlViewDef(string Name, IReadOnlyList<string> Columns, PgQuery Query, string? CheckOption);

internal sealed record SqliteTriggerDef(string Name, string Timing, string Event, IReadOnlyList<string> Columns, PgName Table, PgExpr? When, IReadOnlyList<PgStatement> Body);

internal sealed record RoutineArg(string Mode, string? Name, string Type, PgExpr? Default);

internal sealed record RoutineHeader(PgName Name, bool Procedure, IReadOnlyList<RoutineArg> Args, string? Returns, IReadOnlyList<(string Name, string Type)> ReturnsTable, bool SetOf, string Language, string Body)
{
    public bool ReturnsTrigger => Returns is "trigger";
}
