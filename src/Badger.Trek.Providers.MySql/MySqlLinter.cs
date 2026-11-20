using System.Text.RegularExpressions;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.MySql;

/// <summary>
/// Checks statement by statement after splitting: USE would point trek's journal at another database, mysql
/// client commands (source, \. ...) cannot run outside the mysql client, and a stored program body that ended at its
/// first ';' needs a DELIMITER line. MySQL commits DDL implicitly, so trek runs MySQL changes without a transaction;
/// that is documented rather than linted, since almost every change has DDL.
/// </summary>
public sealed partial class MySqlLinter : IScriptLinter
{
    private readonly MySqlStatementSplitter splitter = new();

    public IReadOnlyList<LintDiagnostic> Lint(string script, LintContext context)
    {
        var diagnostics = new List<LintDiagnostic>();
        foreach (var batch in splitter.Analyze(script).Batches)
        {
            var text = StripComments(batch.Text);
            var line = batch.StartLine + LeadingLines(batch.Text);

            if (UseRegex().IsMatch(text))
            {
                diagnostics.Add(new(LintSeverity.Error, "USE switches the session to another database: the rest of the change, and trek's journal entry for it, would go there. Qualify names with the database instead (other_db.table), or remove it.", line, "TREK009"));
            }

            if (ClientCommandRegex().Match(text) is { Success: true } c)
            {
                diagnostics.Add(new(LintSeverity.Error, $"'{Collapse(c.Value)}' is a mysql client command, not SQL; trek cannot run it. Put the SQL in the change itself.", line, "TREK005"));
            }

            if (StoredProgramRegex().IsMatch(text) && BeginRegex().IsMatch(text) && !EndsWithEndRegex().IsMatch(text))
            {
                diagnostics.Add(new(LintSeverity.Error, "This stored program's body ends at its first ';'. Set a delimiter around it, as the mysql client needs:\n  DELIMITER //\n  CREATE PROCEDURE ... BEGIN ...; ...; END //\n  DELIMITER ;", line, "TREK005"));
            }
        }

        return diagnostics.OrderBy(d => d.Line).ToList();
    }

    private static string StripComments(string sql) => CommentRegex().Replace(sql, " ").Trim();

    private static int LeadingLines(string sql)
    {
        var m = LeadingCommentRegex().Match(sql);
        return m.Success ? m.Value.Count(c => c == '\n') : 0;
    }

    private static string Collapse(string text) => WhitespaceRegex().Replace(text.Trim(), " ");

    [GeneratedRegex(@"(?:--\s|#)[^\n]*|/\*(?!!).*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"\A(?:\s+|(?:--\s|#)[^\n]*\n|/\*(?!!).*?\*/)*", RegexOptions.Singleline)]
    private static partial Regex LeadingCommentRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\AUSE\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex UseRegex();

    [GeneratedRegex(@"\A(?:source\s+\S+|\\[.!a-zA-Z]\S*)", RegexOptions.IgnoreCase)]
    private static partial Regex ClientCommandRegex();

    [GeneratedRegex(@"\ACREATE\s+(?:OR\s+REPLACE\s+)?(?:DEFINER\s*=\s*\S+\s+)?(?:PROCEDURE|FUNCTION|TRIGGER|EVENT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StoredProgramRegex();

    [GeneratedRegex(@"\bBEGIN\b", RegexOptions.IgnoreCase)]
    private static partial Regex BeginRegex();

    [GeneratedRegex(@"\bEND\s*\z", RegexOptions.IgnoreCase)]
    private static partial Regex EndsWithEndRegex();
}
