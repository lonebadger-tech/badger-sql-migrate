using Badger.SqlMigrate.Core.Plan;

namespace Badger.SqlMigrate.Core.Providers;

/// <summary>Splits a script into batches the driver can execute (GO for T-SQL, statements for others).</summary>
public interface IScriptParser
{
    IReadOnlyList<ScriptBatch> Split(string script);
}

/// <param name="Text">SQL to send.</param>
/// <param name="StartLine">1-based line in the original script, for error messages.</param>
/// <param name="RepeatCount">How many times to execute (T-SQL "GO n").</param>
public sealed record ScriptBatch(string Text, int StartLine, int RepeatCount = 1);

public enum LintSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record LintDiagnostic(LintSeverity Severity, string Message, int Line, string Code);

public sealed record LintContext(string ChangeName, string ScriptKind, ChangeFlags Flags);

/// <summary>Static checks on scripts, e.g. statements that cannot run in a transaction without !notx.</summary>
public interface IScriptLinter
{
    IReadOnlyList<LintDiagnostic> Lint(string script, LintContext context);
}

/// <summary>Holds an exclusive deploy lock for the whole run so concurrent deployers cannot interleave.</summary>
public interface IDeployLock
{
    Task<IAsyncDisposable> AcquireAsync(IDatabaseSession session, string stateSchema, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Produces a canonical, ordered text dump of the schema whose hash is the schema fingerprint.</summary>
public interface ISchemaInspector
{
    Task<string> CaptureAsync(IDatabaseSession session, string stateSchema, CancellationToken cancellationToken);

    /// <summary>
    /// Version of the dump format. Bump it when the dump changes for an unchanged schema; snapshots recorded with
    /// another version are then not reported as drift (they are re-recorded at the next deploy).
    /// </summary>
    int FormatVersion => 1;

    /// <summary>
    /// Splits a dump from <see cref="CaptureAsync"/> into entries with a stable identity (e.g. "column dbo.Customer.Email"),
    /// so 'bsm diff-schema' can report an object as changed rather than removed and added. Default: one entry per line.
    /// </summary>
    IReadOnlyList<SchemaEntry> Parse(string dump) =>
        dump.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => new SchemaEntry(line, line)).ToList();
}

/// <param name="Key">Identity of the object, unique within a dump.</param>
/// <param name="Text">Its full canonical definition (may span lines).</param>
public sealed record SchemaEntry(string Key, string Text);
