using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>
/// ScriptDom-based checks. The main job is catching statements SQL Server refuses inside a user
/// transaction, which would otherwise fail halfway through a deploy, unless the change is marked !notx.
/// </summary>
public sealed partial class TSqlLinter : IScriptLinter
{
    public IReadOnlyList<LintDiagnostic> Lint(string script, LintContext context)
    {
        var diagnostics = new List<LintDiagnostic>();

        // Unrendered variables ({name}, $(name)) are not T-SQL; replace them with a harmless identifier so the parser can run.
        var parseable = VariableRegex().Replace(script, "badger_var");
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(parseable);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0)
        {
            foreach (var error in errors.Take(3))
            {
                diagnostics.Add(new(LintSeverity.Warning, $"T-SQL parse error, lint skipped: {error.Message}", error.Line, "BSM000"));
            }

            return diagnostics;
        }

        var visitor = new Visitor(context, diagnostics);
        fragment.Accept(visitor);
        return diagnostics;
    }

    [GeneratedRegex(@"\$\([A-Za-z_][A-Za-z0-9_.]*\)|\{\{?[A-Za-z_][A-Za-z0-9_.]*\}\}?")]
    private static partial Regex VariableRegex();

    private sealed class Visitor(LintContext context, List<LintDiagnostic> diagnostics) : TSqlFragmentVisitor
    {
        private bool Transactional => !context.Flags.HasFlag(ChangeFlags.NoTransaction);

        public override void Visit(AlterDatabaseStatement node) => NotInTransaction(node, "ALTER DATABASE");

        public override void Visit(CreateDatabaseStatement node) => NotInTransaction(node, "CREATE DATABASE");

        public override void Visit(DropDatabaseStatement node) => NotInTransaction(node, "DROP DATABASE");

        public override void Visit(BackupStatement node) => NotInTransaction(node, "BACKUP");

        public override void Visit(RestoreStatement node) => NotInTransaction(node, "RESTORE");

        public override void Visit(CreateFullTextCatalogStatement node) => NotInTransaction(node, "CREATE FULLTEXT CATALOG");

        public override void Visit(AlterFullTextCatalogStatement node) => NotInTransaction(node, "ALTER FULLTEXT CATALOG");

        public override void Visit(DropFullTextCatalogStatement node) => NotInTransaction(node, "DROP FULLTEXT CATALOG");

        public override void Visit(CreateFullTextIndexStatement node) => NotInTransaction(node, "CREATE FULLTEXT INDEX");

        public override void Visit(AlterFullTextIndexStatement node) => NotInTransaction(node, "ALTER FULLTEXT INDEX");

        public override void Visit(DropFullTextIndexStatement node) => NotInTransaction(node, "DROP FULLTEXT INDEX");

        public override void Visit(ReconfigureStatement node) => NotInTransaction(node, "RECONFIGURE");

        public override void Visit(UseStatement node) =>
            diagnostics.Add(new(LintSeverity.Error, "USE changes the database context; badger deploys to the profile's database. Remove it.", node.StartLine, "BSM002"));

        public override void Visit(BeginTransactionStatement node)
        {
            if (Transactional)
            {
                diagnostics.Add(new(LintSeverity.Warning, "Explicit BEGIN TRANSACTION inside a change that badger already wraps in a transaction. A ROLLBACK here would abort badger's transaction; mark the change !notx if you manage transactions yourself.", node.StartLine, "BSM003"));
            }
        }

        public override void Visit(CreateIndexStatement node)
        {
            var online = node.IndexOptions.OfType<OnlineIndexOption>().Any(o => o.OptionState == OptionState.On);
            var resumable = node.IndexOptions.OfType<IndexStateOption>().Any(o => o.OptionKind == IndexOptionKind.Resumable && o.OptionState == OptionState.On);
            if (online && resumable)
            {
                NotInTransaction(node, "CREATE INDEX ... WITH (RESUMABLE = ON)");
            }
        }

        private void NotInTransaction(TSqlFragment node, string statement)
        {
            if (Transactional)
            {
                diagnostics.Add(new(LintSeverity.Error, $"{statement} cannot run inside a transaction. Mark '{context.ChangeName}' with !notx in badger.plan and make the script safe to re-run.", node.StartLine, "BSM001"));
            }
        }
    }
}
