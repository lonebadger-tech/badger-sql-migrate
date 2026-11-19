using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.Sqlite;

/// <summary>
/// Checks statement by statement: statements SQLite refuses inside a transaction need !notx, explicit transaction
/// control would end bsm's transaction, PRAGMA foreign_keys does nothing inside one (so it needs !notx too), and
/// sqlite3 dot-commands are reported.
/// </summary>
public sealed partial class SqliteLinter : IScriptLinter
{
    private readonly SqliteStatementSplitter splitter = new();

    public IReadOnlyList<LintDiagnostic> Lint(string script, LintContext context)
    {
        var diagnostics = new List<LintDiagnostic>();
        var transactional = !context.Flags.HasFlag(ChangeFlags.NoTransaction);
        var scan = splitter.Analyze(script);
        foreach (var (line, message) in scan.Problems)
        {
            diagnostics.Add(new(LintSeverity.Error, message, line, "BSM005"));
        }

        foreach (var batch in scan.Batches)
        {
            var text = SqliteLexer.Normalize(batch.Text);
            if (!transactional)
            {
                continue;
            }

            if (NonTransactionalRegex().Match(text) is { Success: true } m)
            {
                diagnostics.Add(new(LintSeverity.Error, $"{m.Value.ToUpperInvariant()} cannot run inside a transaction. Mark '{context.ChangeName}' with !notx in bsm.plan and make the script safe to re-run.", batch.StartLine, "BSM001"));
            }

            if (TransactionControlRegex().Match(text) is { Success: true } t)
            {
                diagnostics.Add(new(LintSeverity.Error, $"{t.Value.ToUpperInvariant()} inside a change that bsm already runs in a transaction would end bsm's transaction. Remove it, or mark the change !notx if it manages its own transactions.", batch.StartLine, "BSM003"));
            }

            if (ForeignKeysPragmaRegex().IsMatch(text))
            {
                diagnostics.Add(new(LintSeverity.Error, $"PRAGMA foreign_keys does nothing inside a transaction. To change tables with foreign keys switched off, mark '{context.ChangeName}' with !notx and write PRAGMA foreign_keys = OFF; BEGIN; ... COMMIT; PRAGMA foreign_keys = ON;", batch.StartLine, "BSM001"));
            }
        }

        return diagnostics.OrderBy(d => d.Line).ToList();
    }

    [GeneratedRegex(@"\A(?:VACUUM|ATTACH|DETACH|PRAGMA(?:\s*\w+\s*\.)?\s*journal_mode)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NonTransactionalRegex();

    [GeneratedRegex(@"\A(?:BEGIN|COMMIT|END|ROLLBACK(?!\s*(?:TRANSACTION\s*)?TO\b))\b", RegexOptions.IgnoreCase)]
    private static partial Regex TransactionControlRegex();

    [GeneratedRegex(@"\APRAGMA(?:\s*\w+\s*\.)?\s*foreign_keys\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForeignKeysPragmaRegex();
}
