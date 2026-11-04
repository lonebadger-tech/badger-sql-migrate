namespace Badger.SqlMigrate.Core.Import;

/// <summary>What a statement in a SQL project script creates (or, for attachments, changes).</summary>
public enum SqlObjectKind
{
    Schema,
    User,
    Role,
    Assembly,
    PartitionFunction,
    PartitionScheme,
    XmlSchemaCollection,
    Type,
    Sequence,
    Table,
    Synonym,
    Function,
    View,
    Procedure,
    Trigger,

    // Attachments: they belong to an object created earlier in the same file when there is one.
    Index,
    Constraint,
    FullText,
    Permission,
    RoleMembership,
    ExtendedProperty,
    Other,
}

/// <summary>What a reference names: a schema-scoped object, a database principal, a schema, or a partition function/scheme.</summary>
public enum SqlReferenceKind
{
    Object,
    Principal,
    Schema,
    Partition,
}

/// <param name="Schema">Null for unqualified names (the project's default schema applies) and for non-schema-scoped names.</param>
/// <param name="Name">The name as written, unquoted (may contain $(var) text).</param>
public sealed record SqlObjectName(string? Schema, string Name)
{
    public override string ToString() => Schema is null ? Name : $"{Schema}.{Name}";
}

public sealed record SqlReference(SqlReferenceKind Kind, SqlObjectName Name);

/// <summary>A text edit at an absolute offset of the parsed script.</summary>
public sealed record SqlTextEdit(int Offset, int Length, string Replacement);

/// <summary>A FOREIGN KEY constraint written inside CREATE TABLE, and how to move it to a later ALTER TABLE.</summary>
/// <param name="Name">The constraint name, or null when SQL Server names it.</param>
/// <param name="Removal">Removes the constraint (and its comma) from the CREATE TABLE text.</param>
/// <param name="AddStatement">ALTER TABLE ... ADD ... FOREIGN KEY, from the original text.</param>
/// <param name="DropStatement">Drops the constraint again (looks the name up when SQL Server named it).</param>
public sealed record SqlForeignKey(string? Name, SqlObjectName References, SqlTextEdit Removal, string AddStatement, string DropStatement);

/// <summary>One statement of a SQL project script.</summary>
public sealed record SqlStatementInfo
{
    public required SqlObjectKind Kind { get; init; }

    /// <summary>
    /// The object created; for attachments (index, constraint, permission, extended property, ALTER TABLE ...) the
    /// object they belong to, when known.
    /// </summary>
    public SqlObjectName? Target { get; init; }

    /// <summary>The index or constraint name, for attachments that have one.</summary>
    public string? ChildName { get; init; }

    /// <summary>Absolute offset and length of the statement in the parsed text.</summary>
    public required int Offset { get; init; }

    public required int Length { get; init; }

    /// <summary>The batch (GO-separated) the statement is in.</summary>
    public required int Batch { get; init; }

    public IReadOnlyList<SqlReference> References { get; init; } = [];

    /// <summary>For modules written with CREATE: inserts " OR ALTER" after the CREATE keyword.</summary>
    public SqlTextEdit? CreateOrAlter { get; init; }

    /// <summary>WITH SCHEMABINDING (views, functions).</summary>
    public bool SchemaBound { get; init; }

    /// <summary>
    /// Created with deferred name resolution: procedures, triggers and non-inline functions can be created before the
    /// tables, views and procedures they use exist.
    /// </summary>
    public bool Deferred { get; init; }

    /// <summary>T-SQL that undoes this statement, or null when it cannot be generated.</summary>
    public string? Revert { get; init; }

    public IReadOnlyList<SqlForeignKey> ForeignKeys { get; init; } = [];
}

public sealed record SqlParseResult(IReadOnlyList<SqlStatementInfo> Statements, IReadOnlyList<string> Errors);

/// <summary>
/// Parses SQL project scripts into objects and references. Implemented by the SQL Server provider (with ScriptDom),
/// so Core needs no T-SQL parser of its own.
/// </summary>
public interface ISqlObjectParser
{
    /// <summary>Parses <paramref name="script"/>; $(name) sqlcmd variables are allowed anywhere.</summary>
    SqlParseResult Parse(string script);
}
