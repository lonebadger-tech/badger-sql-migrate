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
}
