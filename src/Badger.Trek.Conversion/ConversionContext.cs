using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion;

public enum Dialect
{
    PostgreSql,
    SqlServer,
    Sqlite,
    MySql,
}

public sealed record ConversionOptions
{
    /// <summary>The provider id of the target ("postgresql" or "sqlserver").</summary>
    public required string TargetProvider { get; init; }

    /// <summary>Null = Lower when converting to PostgreSQL, Keep when converting to SQL Server.</summary>
    public NameStyle? Names { get; init; }

    /// <summary>Do not give PostgreSQL text columns a case-insensitive collation when the SQL Server source compares case-insensitively.</summary>
    public bool CaseSensitive { get; init; }
}

/// <summary>Something a person has to look at, attached to the object it concerns.</summary>
public sealed record ConversionIssue(string Object, string Message);

/// <summary>State shared by the type mapper, the translators and the DDL writer during one conversion.</summary>
public sealed class ConversionContext
{
    private readonly Dictionary<string, PortableEnum> enums;
    private readonly Dictionary<string, PortableDomain> domains;

    public ConversionContext(PortableSchema schema, ConversionOptions options)
    {
        Schema = schema;
        Source = DialectOf(schema.SourceProvider);
        Target = DialectOf(options.TargetProvider);
        if (Source == Target)
        {
            throw new Core.MigrationException($"The database is already {options.TargetProvider}; convert to the other provider.");
        }

        var style = options.Names ?? (Target == Dialect.PostgreSql ? NameStyle.Lower : NameStyle.Keep);
        Names = new NameMapper(schema.DefaultSchema, DefaultSchemaOf(Target), style);
        CaseInsensitiveCollation = Target is Dialect.PostgreSql or Dialect.Sqlite && schema.CaseInsensitive && !options.CaseSensitive;
        enums = schema.Enums.ToDictionary(e => e.Name.Key, StringComparer.OrdinalIgnoreCase);
        domains = schema.Domains.ToDictionary(d => d.Name.Key, StringComparer.OrdinalIgnoreCase);

        // A column name counts as text (or boolean) when every column of that name is: translators use it to tell string
        // concatenation from addition and 1 from true without a full type checker.
        foreach (var group in schema.Tables.SelectMany(t => t.Columns).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var kinds = group.Select(c => KindOf(c.Type)).Distinct().ToList();
            if (kinds.Count == 1 && kinds[0] != ColumnKind.Other)
            {
                columnKinds[group.Key] = kinds[0];
            }
        }
    }

    private readonly Dictionary<string, ColumnKind> columnKinds = new(StringComparer.OrdinalIgnoreCase);

    public enum ColumnKind
    {
        Other,
        Text,
        Boolean,
    }

    /// <summary>What every column of this name is, across all tables (Other when unknown or mixed).</summary>
    public ColumnKind ColumnKindOf(string columnName) => columnKinds.GetValueOrDefault(columnName);

    /// <summary>Classifies a source-dialect type.</summary>
    public ColumnKind KindOf(string sourceType)
    {
        var t = sourceType.Trim().ToLowerInvariant();
        var name = t.Split('(')[0].Trim();
        if (Source == Dialect.MySql)
        {
            return name is "bool" or "boolean" || t.StartsWith("tinyint(1)", StringComparison.Ordinal) ? ColumnKind.Boolean
                : name is "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set" ? ColumnKind.Text
                : ColumnKind.Other;
        }

        if (Source == Dialect.Sqlite)
        {
            return t.Contains("bool", StringComparison.Ordinal) ? ColumnKind.Boolean
                : t.Contains("char", StringComparison.Ordinal) || t.Contains("clob", StringComparison.Ordinal) || t.Contains("text", StringComparison.Ordinal) ? ColumnKind.Text
                : ColumnKind.Other;
        }

        if (Source == Dialect.SqlServer)
        {
            return name switch
            {
                "bit" => ColumnKind.Boolean,
                "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname" => ColumnKind.Text,
                _ => ColumnKind.Other,
            };
        }

        return name switch
        {
            "boolean" or "bool" => ColumnKind.Boolean,
            "text" or "character varying" or "varchar" or "character" or "char" or "bpchar" or "name" or "citext" => ColumnKind.Text,
            _ when t.EndsWith(".citext", StringComparison.Ordinal) => ColumnKind.Text,
            _ => ColumnKind.Other,
        };
    }

    public PortableSchema Schema { get; }

    public Dialect Source { get; }

    public Dialect Target { get; }

    public NameMapper Names { get; }

    /// <summary>Text columns get the trek_ci collation on PostgreSQL.</summary>
    public bool CaseInsensitiveCollation { get; }

    public const string CaseInsensitiveCollationName = "trek_ci";

    public List<ConversionIssue> Issues { get; } = [];

    /// <summary>"source type → target type" pairs used, for the report.</summary>
    public SortedDictionary<string, string> TypeMappings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Issue(string obj, string message) => Issues.Add(new ConversionIssue(obj, message));

    public PortableEnum? Enum(string schema, string name) => enums.GetValueOrDefault($"{schema}.{name}".ToLowerInvariant());

    public PortableDomain? Domain(string schema, string name) => domains.GetValueOrDefault($"{schema}.{name}".ToLowerInvariant());

    public static string DefaultSchemaOf(Dialect dialect) => dialect switch
    {
        Dialect.PostgreSql => "public",
        Dialect.Sqlite or Dialect.MySql => FlatSchema,
        _ => "dbo",
    };

    /// <summary>The schema of every object in a target without schemas (SQLite; MySQL, where a schema is the database). Never written out.</summary>
    public const string FlatSchema = "main";

    /// <summary>The target has no schemas inside the database: names are flattened.</summary>
    public bool FlatTarget => Target is Dialect.Sqlite or Dialect.MySql;

    /// <summary>
    /// A table, view or other schema object's name in the target. SQLite has no schemas, and in MySQL a schema is the
    /// database: objects outside the source's default schema get the schema as a prefix (sales.Customer becomes
    /// sales_customer), so names never collide.
    /// </summary>
    public PortableName TargetName(PortableName source) => TargetName(source.Schema, source.Name);

    public PortableName TargetName(string schema, string name)
    {
        if (!FlatTarget)
        {
            return new PortableName(Names.Schema(schema), Names.Name(name));
        }

        var flat = string.Equals(schema, Schema.DefaultSchema, StringComparison.OrdinalIgnoreCase) || string.Equals(schema, FlatSchema, StringComparison.OrdinalIgnoreCase)
            ? Names.Name(name)
            : Names.Name(schema + "_" + name);
        return new PortableName(FlatSchema, flat);
    }

    /// <summary>A schema-qualified name in the target, quoted as needed (SQLite and MySQL: the bare name).</summary>
    public string Qualified(PortableName name)
    {
        var target = TargetName(name);
        return FlatTarget ? Quote(target.Name) : $"{Quote(target.Schema)}.{Quote(target.Name)}";
    }

    public string QualifiedTarget(string schema, string name) => FlatTarget ? Quote(name) : $"{Quote(schema)}.{Quote(name)}";

    /// <summary>A target identifier, quoted when the target dialect needs it.</summary>
    public string Quote(string name) => Target switch
    {
        Dialect.PostgreSql => Providers.PostgreSql.Scripting.Sql.Ident(name),
        Dialect.Sqlite => SqliteQuote(name),
        Dialect.MySql => MySqlQuote(name),
        _ => TSqlQuote(name),
    };

    /// <summary>MySQL: always backquoted (its reserved words change between releases).</summary>
    public static string MySqlQuote(string name) => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";

    /// <summary>A MySQL string literal: backslash is an escape character in MySQL's default SQL mode.</summary>
    public static string MySqlLiteral(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string SqliteQuote(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") && !SqliteReserved.Contains(name) ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static readonly HashSet<string> SqliteReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "abort", "action", "add", "after", "all", "alter", "always", "analyze", "and", "as", "asc", "attach", "autoincrement", "before", "begin", "between", "by",
        "cascade", "case", "cast", "check", "collate", "column", "commit", "conflict", "constraint", "create", "cross", "current", "current_date", "current_time",
        "current_timestamp", "database", "default", "deferrable", "deferred", "delete", "desc", "detach", "distinct", "do", "drop", "each", "else", "end", "escape",
        "except", "exclude", "exclusive", "exists", "explain", "fail", "filter", "first", "following", "for", "foreign", "from", "full", "generated", "glob", "group",
        "groups", "having", "if", "ignore", "immediate", "in", "index", "indexed", "initially", "inner", "insert", "instead", "intersect", "into", "is", "isnull",
        "join", "key", "last", "left", "like", "limit", "match", "materialized", "natural", "no", "not", "nothing", "notnull", "null", "nulls", "of", "offset", "on",
        "or", "order", "others", "outer", "over", "partition", "plan", "pragma", "preceding", "primary", "query", "raise", "range", "recursive", "references",
        "regexp", "reindex", "release", "rename", "replace", "restrict", "returning", "right", "rollback", "row", "rows", "savepoint", "select", "set", "table",
        "temp", "temporary", "then", "ties", "to", "transaction", "trigger", "unbounded", "union", "unique", "update", "using", "vacuum", "values", "view",
        "virtual", "when", "where", "window", "with", "without",
    };

    /// <summary>Source name mapped and quoted for the target.</summary>
    public string Ident(string sourceName) => Quote(Names.Name(sourceName));

    public static string TSqlQuote(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_@#][A-Za-z0-9_@#$]*$") && !TSqlReserved.Contains(name) ? name : Providers.SqlServer.Scripting.Sql.Quote(name);

    public string Literal(string text) => Target switch
    {
        Dialect.SqlServer => Providers.SqlServer.Scripting.Sql.Literal(text),
        Dialect.MySql => MySqlLiteral(text),
        _ => Providers.PostgreSql.Scripting.Sql.Literal(text),
    };

    public static Dialect DialectOf(string provider) => provider.ToLowerInvariant() switch
    {
        "postgresql" or "postgres" => Dialect.PostgreSql,
        "sqlserver" => Dialect.SqlServer,
        "sqlite" or "sqlite3" => Dialect.Sqlite,
        "mysql" => Dialect.MySql,
        _ => throw new Core.MigrationException($"trek convert works between sqlserver, postgresql, mysql and sqlite, not '{provider}'."),
    };

    public static string ProviderOf(Dialect dialect) => dialect switch
    {
        Dialect.PostgreSql => "postgresql",
        Dialect.Sqlite => "sqlite",
        Dialect.MySql => "mysql",
        _ => "sqlserver",
    };

    private static readonly HashSet<string> TSqlReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUTHORIZATION", "BACKUP", "BEGIN", "BETWEEN", "BREAK", "BROWSE", "BULK", "BY", "CASCADE", "CASE", "CHECK",
        "CHECKPOINT", "CLOSE", "CLUSTERED", "COALESCE", "COLLATE", "COLUMN", "COMMIT", "COMPUTE", "CONSTRAINT", "CONTAINS", "CONTAINSTABLE", "CONTINUE", "CONVERT",
        "CREATE", "CROSS", "CURRENT", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "CURSOR", "DATABASE", "DBCC", "DEALLOCATE", "DECLARE",
        "DEFAULT", "DELETE", "DENY", "DESC", "DISK", "DISTINCT", "DISTRIBUTED", "DOUBLE", "DROP", "DUMP", "ELSE", "END", "ERRLVL", "ESCAPE", "EXCEPT", "EXEC",
        "EXECUTE", "EXISTS", "EXIT", "EXTERNAL", "FETCH", "FILE", "FILLFACTOR", "FOR", "FOREIGN", "FREETEXT", "FREETEXTTABLE", "FROM", "FULL", "FUNCTION", "GOTO",
        "GRANT", "GROUP", "HAVING", "HOLDLOCK", "IDENTITY", "IDENTITY_INSERT", "IDENTITYCOL", "IF", "IN", "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS",
        "JOIN", "KEY", "KILL", "LEFT", "LIKE", "LINENO", "LOAD", "MERGE", "NATIONAL", "NOCHECK", "NONCLUSTERED", "NOT", "NULL", "NULLIF", "OF", "OFF", "OFFSETS",
        "ON", "OPEN", "OPENDATASOURCE", "OPENQUERY", "OPENROWSET", "OPENXML", "OPTION", "OR", "ORDER", "OUTER", "OVER", "PERCENT", "PIVOT", "PLAN", "PRECISION",
        "PRIMARY", "PRINT", "PROC", "PROCEDURE", "PUBLIC", "RAISERROR", "READ", "READTEXT", "RECONFIGURE", "REFERENCES", "REPLICATION", "RESTORE", "RESTRICT",
        "RETURN", "REVERT", "REVOKE", "RIGHT", "ROLLBACK", "ROWCOUNT", "ROWGUIDCOL", "RULE", "SAVE", "SCHEMA", "SECURITYAUDIT", "SELECT", "SEMANTICKEYPHRASETABLE",
        "SESSION_USER", "SET", "SETUSER", "SHUTDOWN", "SOME", "STATISTICS", "SYSTEM_USER", "TABLE", "TABLESAMPLE", "TEXTSIZE", "THEN", "TO", "TOP", "TRAN",
        "TRANSACTION", "TRIGGER", "TRUNCATE", "TRY_CONVERT", "TSEQUAL", "UNION", "UNIQUE", "UNPIVOT", "UPDATE", "UPDATETEXT", "USE", "USER", "VALUES", "VARYING",
        "VIEW", "WAITFOR", "WHEN", "WHERE", "WHILE", "WITH", "WITHIN", "WRITETEXT",
    };
}
