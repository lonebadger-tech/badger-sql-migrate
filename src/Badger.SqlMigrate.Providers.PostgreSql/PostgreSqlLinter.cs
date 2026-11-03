using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.PostgreSql;

/// <summary>
/// Checks statement by statement (after splitting): statements PostgreSQL refuses inside a transaction block
/// need !notx, explicit transaction control would end bsm's transaction, and psql-only syntax is not SQL.
/// </summary>
public sealed partial class PostgreSqlLinter : IScriptLinter
{
    private readonly PostgreSqlStatementSplitter splitter = new();

    public IReadOnlyList<LintDiagnostic> Lint(string script, LintContext context)
    {
        var diagnostics = new List<LintDiagnostic>();
        var transactional = !context.Flags.HasFlag(ChangeFlags.NoTransaction);
        foreach (var batch in splitter.Split(script))
        {
            var text = StripComments(batch.Text);
            var line = batch.StartLine + LeadingLines(batch.Text);

            if (MetaCommandRegex().Match(text) is { Success: true } meta)
            {
                diagnostics.Add(new(LintSeverity.Error, $"'{meta.Value.Trim()}' is a psql meta-command, not SQL; bsm sends statements to the server directly. Remove it.", line, "BSM005"));
                continue;
            }

            if (CopyFromStdinRegex().IsMatch(text))
            {
                diagnostics.Add(new(LintSeverity.Error, "COPY ... FROM STDIN needs psql to stream the data. Use INSERT statements, or COPY from a file the server can read.", line, "BSM006"));
            }

            if (transactional && NonTransactionalRegex().Match(text) is { Success: true } m)
            {
                diagnostics.Add(new(LintSeverity.Error, $"{Collapse(m.Value)} cannot run inside a transaction block. Mark '{context.ChangeName}' with !notx in bsm.plan and make the script safe to re-run.", line, "BSM001"));
            }

            if (transactional && TransactionControlRegex().Match(text) is { Success: true } t)
            {
                diagnostics.Add(new(LintSeverity.Error, $"{Collapse(t.Value).ToUpperInvariant()} inside a change that bsm already runs in a transaction would end bsm's transaction. Remove it, or mark the change !notx if it manages its own transactions.", line, "BSM003"));
            }

            if (transactional && EnumAddValueRegex().IsMatch(text))
            {
                diagnostics.Add(new(LintSeverity.Warning, "A value added with ALTER TYPE ... ADD VALUE cannot be used until the transaction commits; statements later in this change that use it will fail. Put them in a later change.", line, "BSM007"));
            }
        }

        return diagnostics;
    }

    private static string StripComments(string sql) => CommentRegex().Replace(sql, " ").Trim();

    private static int LeadingLines(string sql)
    {
        var m = LeadingCommentRegex().Match(sql);
        return m.Success ? m.Value.Count(c => c == '\n') : 0;
    }

    private static string Collapse(string text) => WhitespaceRegex().Replace(text.Trim(), " ");

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"\A(?:\s+|--[^\n]*\n|/\*.*?\*/)*", RegexOptions.Singleline)]
    private static partial Regex LeadingCommentRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\A\\[a-zA-Z!?]+[^\n]*")]
    private static partial Regex MetaCommandRegex();

    [GeneratedRegex(@"\ACOPY\b[\s\S]*\bFROM\s+STDIN\b", RegexOptions.IgnoreCase)]
    private static partial Regex CopyFromStdinRegex();

    [GeneratedRegex(@"\A(?:CREATE\s+(?:UNIQUE\s+)?INDEX\s+CONCURRENTLY|DROP\s+INDEX\s+CONCURRENTLY|REINDEX\b[^;]*\bCONCURRENTLY|VACUUM|CREATE\s+DATABASE|DROP\s+DATABASE|ALTER\s+SYSTEM|CREATE\s+TABLESPACE|DROP\s+TABLESPACE|CREATE\s+SUBSCRIPTION|DROP\s+SUBSCRIPTION|ALTER\s+DATABASE\b[^;]*\bSET\s+TABLESPACE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NonTransactionalRegex();

    [GeneratedRegex(@"\A(?:BEGIN|START\s+TRANSACTION|COMMIT|END|ABORT|ROLLBACK(?!\s+TO\b)(?!\s+PREPARED\b))\b(?!\s*;?\s*\$)", RegexOptions.IgnoreCase)]
    private static partial Regex TransactionControlRegex();

    [GeneratedRegex(@"\AALTER\s+TYPE\b[^;]*\bADD\s+VALUE\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnumAddValueRegex();
}
