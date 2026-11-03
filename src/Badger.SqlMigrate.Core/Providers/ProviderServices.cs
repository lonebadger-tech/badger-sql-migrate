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

/// <summary>What to leave out when scripting differences, and which objects repeatables own.</summary>
public sealed record SchemaScriptOptions
{
    public required string StateSchema { get; init; }

    /// <summary>"schema.name" globs (* and ?) of objects to ignore, e.g. other tools' journal tables.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>The project's repeatable scripts: objects they define are maintained there, not in generated changes.</summary>
    public IReadOnlyList<string> RepeatableScripts { get; init; } = [];
}

/// <param name="Deploy">Script that turns the first database into the second (empty when they match).</param>
/// <param name="Revert">Script that turns the second back into the first.</param>
/// <param name="Summary">One line per difference ("+ table dbo.Customer", "~ column dbo.Order.Total ...").</param>
/// <param name="Warnings">What was not scripted, or needs a person to check.</param>
/// <param name="DataLoss">The deploy script drops or narrows data.</param>
public sealed record SchemaScript(string Deploy, string Revert, IReadOnlyList<string> Summary, IReadOnlyList<string> Warnings, bool DataLoss)
{
    public bool IsEmpty => Deploy.Length == 0 && Warnings.Count == 0;
}

/// <summary>Writes DDL for the differences between two databases (for 'bsm add --from-diff').</summary>
public interface ISchemaScripter
{
    Task<SchemaScript> ScriptAsync(IDatabaseSession from, IDatabaseSession to, SchemaScriptOptions options, CancellationToken cancellationToken);
}

/// <summary>A temporary database on the same server, dropped on dispose.</summary>
public sealed class ScratchDatabase(Configuration.DeploymentEnvironment environment, Func<ValueTask> drop) : IAsyncDisposable
{
    public Configuration.DeploymentEnvironment Environment { get; } = environment;

    public ValueTask DisposeAsync() => drop();
}

/// <summary>Creates throwaway databases (the shadow database 'bsm add --from-diff' builds from the plan).</summary>
public interface IScratchDatabases
{
    Task<ScratchDatabase> CreateAsync(Configuration.DeploymentEnvironment server, SessionContext context, bool keep, CancellationToken cancellationToken);
}

