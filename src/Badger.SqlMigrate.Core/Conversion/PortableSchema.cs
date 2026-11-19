using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Core.Conversion;

/// <summary>A schema-qualified object name as the source database spells it.</summary>
public sealed record PortableName(string Schema, string Name)
{
    public override string ToString() => $"{Schema}.{Name}";

    public string Key => $"{Schema}.{Name}".ToLowerInvariant();
}

public sealed record PortableIdentity(long Seed, long Increment, bool Always);

/// <param name="Type">The type as the source dialect writes it in DDL, e.g. "nvarchar(100)", "timestamp with time zone".</param>
public sealed record PortableColumn(string Name, string Type, bool Nullable)
{
    /// <summary>Default expression in the source dialect.</summary>
    public string? Default { get; init; }

    public PortableIdentity? Identity { get; init; }

    /// <summary>Computed / generated column expression in the source dialect.</summary>
    public string? Computed { get; init; }

    public bool ComputedStored { get; init; }

    /// <summary>Collation when it differs from the database default.</summary>
    public string? Collation { get; init; }

    public string? Comment { get; init; }
}

public sealed record PortableKey(string? Name, IReadOnlyList<string> Columns);

public sealed record PortableForeignKey(string? Name, IReadOnlyList<string> Columns, PortableName Referenced, IReadOnlyList<string> ReferencedColumns, string OnDelete, string OnUpdate);

/// <param name="Expression">Boolean expression in the source dialect.</param>
public sealed record PortableCheck(string? Name, string Expression);

/// <param name="Text">A column name, or an expression in the source dialect when <paramref name="IsExpression"/>.</param>
public sealed record PortableIndexColumn(string Text, bool IsExpression, bool Descending);

/// <param name="Method">Access method when it is not the default B-tree (gin, gist, hash, columnstore ...).</param>
public sealed record PortableIndex(string Name, bool Unique, IReadOnlyList<PortableIndexColumn> Columns, IReadOnlyList<string> Included, string? Filter, string? Method);

public sealed record PortableTable(
    PortableName Name,
    IReadOnlyList<PortableColumn> Columns,
    PortableKey? PrimaryKey,
    IReadOnlyList<PortableKey> Uniques,
    IReadOnlyList<PortableCheck> Checks,
    IReadOnlyList<PortableForeignKey> ForeignKeys,
    IReadOnlyList<PortableIndex> Indexes)
{
    public string? Comment { get; init; }
}

public sealed record PortableSequence(PortableName Name, string Type, long Start, long Increment, long? Minimum, long? Maximum, bool Cycle);

public sealed record PortableEnum(PortableName Name, IReadOnlyList<string> Labels);

/// <summary>A PostgreSQL domain or a SQL Server alias type.</summary>
public sealed record PortableDomain(PortableName Name, string BaseType, bool Nullable, string? Default, IReadOnlyList<PortableCheck> Checks);

public enum PortableCodeKind
{
    View,
    Function,
    Procedure,
    Trigger,
}

/// <param name="Definition">The full CREATE statement in the source dialect.</param>
public sealed record PortableCode(PortableCodeKind Kind, PortableName Name, string Definition)
{
    /// <summary>For triggers: the table or view the trigger is on.</summary>
    public PortableName? Table { get; init; }

    /// <summary>For PostgreSQL triggers: the trigger function they execute (its code is a separate entry).</summary>
    public PortableName? TriggerFunction { get; init; }

    /// <summary>Routine language (plpgsql, sql, tsql ...).</summary>
    public string? Language { get; init; }

    public bool Materialized { get; init; }

    /// <summary>For table-valued functions: the columns they return (name and source type).</summary>
    public IReadOnlyList<PortableColumn> ResultColumns { get; init; } = [];

    /// <summary>Keys (<see cref="PortableName.Key"/>) of code objects this one uses; they are created first.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

/// <summary>
/// A database's schema in a vendor-neutral shape, read from its catalog. Names are as the source spells them;
/// types and expressions stay in the source dialect for a translator to rewrite.
/// </summary>
public sealed class PortableSchema
{
    public required string SourceProvider { get; init; }

    /// <summary>The source's default schema ("dbo", "public").</summary>
    public required string DefaultSchema { get; init; }

    /// <summary>Text comparisons ignore case by default (SQL Server _CI_ collations).</summary>
    public bool CaseInsensitive { get; init; }

    public string? Collation { get; init; }

    public List<string> Schemas { get; } = [];

    public List<PortableTable> Tables { get; } = [];

    public List<PortableSequence> Sequences { get; } = [];

    public List<PortableEnum> Enums { get; } = [];

    public List<PortableDomain> Domains { get; } = [];

    /// <summary>Views, routines and triggers in an order that creates dependencies first.</summary>
    public List<PortableCode> Code { get; } = [];

    /// <summary>Objects the exporter could not represent.</summary>
    public List<string> Warnings { get; } = [];
}

public sealed record ExportOptions
{
    public required string StateSchema { get; init; }

    /// <summary>Objects to leave out (bsm's journal is always left out).</summary>
    public Func<PortableName, bool> Excluded { get; init; } = _ => false;
}

/// <summary>Reads a database's schema into a <see cref="PortableSchema"/> (for 'bsm convert').</summary>
public interface ISchemaExporter
{
    Task<PortableSchema> ExportAsync(IDatabaseSession session, ExportOptions options, CancellationToken cancellationToken);
}

/// <summary>A column a data transfer reads or writes, with its type in the session's own dialect.</summary>
public sealed record TransferColumn(string Name, string Type);

/// <summary>Streams rows out of and into tables of one vendor (for 'bsm convert data').</summary>
public interface IDataTransfer
{
    /// <summary>The table's columns in order, with their types in this dialect (generated columns left out).</summary>
    Task<IReadOnlyList<TransferColumn>> ColumnsAsync(IDatabaseSession session, PortableName table, CancellationToken cancellationToken);

    Task<long> CountAsync(IDatabaseSession session, PortableName table, CancellationToken cancellationToken);

    IAsyncEnumerable<object?[]> ReadAsync(IDatabaseSession session, PortableName table, IReadOnlyList<TransferColumn> columns, CancellationToken cancellationToken);

    /// <summary>Bulk-loads rows; values are already converted to what the target column expects. Returns rows written.</summary>
    Task<long> WriteAsync(IDatabaseSession session, PortableName table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken cancellationToken);

    Task TruncateAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken);

    /// <summary>Before loading: turn off what would reject rows loaded out of dependency order (foreign keys, triggers).</summary>
    Task BeginLoadAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken);

    /// <summary>After loading: turn constraints and triggers back on, check them, and move identities and sequences past the loaded values.</summary>
    Task EndLoadAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken);
}
