using System.Text.RegularExpressions;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.Oracle;

/// <summary>
/// Checks statement by statement after splitting: SQL*Plus commands trek cannot run (@file, CONNECT, DEFINE ...), ALTER
/// SESSION SET CURRENT_SCHEMA (objects would go to another schema than the one trek fingerprints), a PL/SQL block whose
/// closing '/' line is missing (the next statement would be sent as part of it), and, while oracle.minVersion is 19,
/// syntax only Oracle 26ai runs. Oracle commits DDL implicitly, so trek runs Oracle changes without a transaction; that
/// is documented rather than linted.
/// </summary>
public sealed partial class OracleLinter(OracleOptions options) : IScriptLinter
{
    private readonly OracleStatementSplitter splitter = new();

    public OracleLinter()
        : this(OracleOptions.Default)
    {
    }

    public IReadOnlyList<LintDiagnostic> Lint(string script, LintContext context)
    {
        var diagnostics = new List<LintDiagnostic>();
        foreach (var item in splitter.Analyze(script))
        {
            if (item.Kind == OracleItemKind.Skipped)
            {
                continue;
            }

            var text = OracleStatementSplitter.StripComments(item.Text).Trim();
            var line = item.Line;

            if (ClientCommandRegex().Match(text) is { Success: true } c)
            {
                diagnostics.Add(new(LintSeverity.Error, $"'{Collapse(c.Value)}' is a SQL*Plus command, not SQL; trek cannot run it. Put the SQL in the change itself (and use {{Name}} variables instead of &substitution).", line, "TREK005"));
                continue;
            }

            if (CurrentSchemaRegex().IsMatch(text))
            {
                diagnostics.Add(new(LintSeverity.Error, "ALTER SESSION SET CURRENT_SCHEMA sends the rest of the change to another schema than the environment's. Qualify names with the schema instead (other_schema.table), or set 'schema' for the environment.", line, "TREK009"));
            }

            if (item.Kind == OracleItemKind.PlSql && MissingSlashRegex().Match(text) is { Success: true } next)
            {
                diagnostics.Add(new(LintSeverity.Error, $"A PL/SQL block or stored program needs a line holding only '/' after its END; without it, the {next.Groups[1].Value.ToUpperInvariant()} that follows is sent as part of the block.", line, "TREK005"));
            }

            if (!options.Supports26ai && item.Kind == OracleItemKind.Sql)
            {
                foreach (var (regex, what) in Newer)
                {
                    if (regex.IsMatch(text))
                    {
                        diagnostics.Add(new(LintSeverity.Warning, $"{what} needs Oracle 26ai, and oracle.minVersion is 19 (19c). Write it the 19c way, or set oracle.minVersion: 26 if every database is 26ai.", line, "TREK008"));
                    }
                }
            }
        }

        return diagnostics.OrderBy(d => d.Line).ToList();
    }

    private static readonly (Regex Regex, string What)[] Newer =
    [
        (IfNotExistsRegex(), "IF [NOT] EXISTS"),
        (BooleanColumnRegex(), "A BOOLEAN column"),
        (VectorRegex(), "The VECTOR type"),
        (DomainRegex(), "CREATE DOMAIN"),
        (DualityRegex(), "A JSON relational duality view"),
        (AnnotationsRegex(), "ANNOTATIONS"),
        (BooleanLiteralRegex(), "TRUE and FALSE in SQL"),
    ];

    private static string Collapse(string text) => WhitespaceRegex().Replace(text.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\A(?:@@?\S*|(?:START|CONNECT|CONN|DEFINE|DEF|UNDEFINE|UNDEF|ACCEPT|ACC|VARIABLE|VAR|PRINT|EXIT|QUIT|HOST|DESCRIBE|DESC|SAVE|GET|EDIT|ED|PASSWORD|STORE|COPY|ATTRIBUTE|SQLPLUS)\b[^\n]*|![^\n]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ClientCommandRegex();

    [GeneratedRegex(@"\AALTER\s+SESSION\s+SET\s+CURRENT_SCHEMA\b", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentSchemaRegex();

    [GeneratedRegex(@"\bEND\b[^;']*;\s*\n\s*(CREATE|ALTER|DROP|INSERT|UPDATE|DELETE|MERGE|GRANT|REVOKE|COMMENT|BEGIN|DECLARE|TRUNCATE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MissingSlashRegex();

    [GeneratedRegex(@"\A(?:CREATE\b[^;]*?\bIF\s+NOT\s+EXISTS|DROP\s+\w+(?:\s+\w+)?\s+IF\s+EXISTS)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IfNotExistsRegex();

    [GeneratedRegex(@"\A(?:CREATE|ALTER)\s+TABLE\b.*?[\(,]\s*""?\w+""?\s+BOOL(?:EAN)?\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BooleanColumnRegex();

    [GeneratedRegex(@"\A(?:CREATE|ALTER)\s+TABLE\b.*?\bVECTOR\s*\(", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex VectorRegex();

    [GeneratedRegex(@"\ACREATE\s+(?:OR\s+REPLACE\s+)?(?:USECASE\s+)?(?:FLEXIBLE\s+)?DOMAIN\b", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRegex();

    [GeneratedRegex(@"\ACREATE\s+(?:OR\s+REPLACE\s+)?(?:FORCE\s+)?(?:EDITIONABLE\s+|NONEDITIONABLE\s+)?JSON\s+(?:RELATIONAL\s+)?DUALITY\s+VIEW\b", RegexOptions.IgnoreCase)]
    private static partial Regex DualityRegex();

    [GeneratedRegex(@"\bANNOTATIONS\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex AnnotationsRegex();

    [GeneratedRegex(@"\A(?:SELECT|INSERT|UPDATE|DELETE|MERGE|CREATE\s+(?:OR\s+REPLACE\s+)?(?:FORCE\s+)?VIEW)\b(?:[^']|'[^']*')*?(?<![\w$#.""])(?:TRUE|FALSE)(?![\w$#""])", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BooleanLiteralRegex();
}
