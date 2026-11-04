using Badger.SqlMigrate.Core.Import;
using Badger.SqlMigrate.Core.Project;

namespace Badger.SqlMigrate.Core.Tests;

public sealed class RepeatableDefinitionsTests
{
    private static IReadOnlyList<string> Match(string script, params string[] live)
    {
        using var project = new TestProject().Repeatable("views/v.sql", script);
        var loaded = MigrationProject.Load(project.Root, "fake");
        return RepeatableDefinitions.Matching(loaded, live, r => r.Script);
    }

    [Fact]
    public void Create_or_alter_whitespace_line_endings_and_requires_headers_do_not_matter()
    {
        const string live = "create VIEW dbo.v\r\nAS SELECT 'active' AS s";
        Assert.Single(Match("-- bsm:requires views/a.sql\nGO\nCREATE OR ALTER VIEW dbo.v\nAS   SELECT 'active' AS s;\n", live));
        Assert.Single(Match("CREATE OR ALTER VIEW dbo.v AS SELECT 'active' AS s\nGO\nGRANT SELECT ON dbo.v TO r;\n", live));
    }

    [Fact]
    public void Any_other_case_change_is_a_difference()
    {
        // A literal whose case changed must re-run, or the database keeps the old text.
        Assert.Empty(Match("CREATE OR ALTER VIEW dbo.v AS SELECT 'Active' AS s", "CREATE VIEW dbo.v AS SELECT 'active' AS s"));
    }
}
