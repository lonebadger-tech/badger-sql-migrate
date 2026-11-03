using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Hashing;
using Badger.SqlMigrate.Core.Import;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Project;

namespace Badger.SqlMigrate.Core.Tests;

public sealed class GrateSqitchImportTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "bsm-import-tests", Guid.NewGuid().ToString("N"));

    public GrateSqitchImportTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private MigrationProject WriteAndLoad(ImportedProject imported, string? environment = null)
    {
        var target = Path.Combine(dir, "out");
        imported.WriteScripts(target);
        return MigrationProject.Load(target, "fake", environment);
    }

    // text_hash values recorded by grate 2.1.6 (after token replacement; DatabaseName = GrDev).
    [Theory]
    [InlineData("-- Customer table for GrDev\nCREATE TABLE dbo.Customer (Id int NOT NULL CONSTRAINT PK_Customer PRIMARY KEY, Name nvarchar(100) NOT NULL, Tag nvarchar(20) NOT NULL CONSTRAINT DF_Customer_Tag DEFAULT (N'{x}'));\n", "mE097pFKfqfytzYE9AigPA==")]
    [InlineData("CREATE TABLE dbo.[Order] (Id int NOT NULL CONSTRAINT PK_Order PRIMARY KEY, CustomerId int NOT NULL CONSTRAINT FK_Order_Customer REFERENCES dbo.Customer (Id), Note nvarchar(50) NULL CONSTRAINT DF_Order_Note DEFAULT ('it''s'));\n", "2gxSgO2CICaz8x9Tg8gu1Q==")]
    public void Grate_hash_matches_grate(string script, string hash)
    {
        Assert.True(GrateImporter.HashMatches(script, hash));
        Assert.True(GrateImporter.HashMatches(script.Replace("\n", "\r\n", StringComparison.Ordinal), hash));
        Assert.False(GrateImporter.HashMatches(script + " ", hash));
        Assert.False(GrateImporter.HashMatches(script, null));
    }

    // Flyway 10.22 checksummed this repeatable after replacing ${appSchema} with "sales".
    [Fact]
    public void Flyway_repeatable_checksum_covers_the_rendered_text() =>
        Assert.Equal(-1828340351, Crc32.Flyway("CREATE OR ALTER VIEW sales.vCustomer AS SELECT Id, Name, Email FROM sales.Customer;\n"));

    [Fact]
    public void Grate_folders_map_to_changes_and_ordered_repeatables()
    {
        Write("up/0002_orders.sql", "CREATE TABLE o (Id int); -- {{DatabaseName}} on {{ServerName}}");
        Write("up/0001_customer.sql", "CREATE TABLE c (Id int, Tag nvarchar(9) DEFAULT '{x}'); -- {{EnvironmentName}} {{UpFolderName}} {{AppRole}}");
        Write("up/sub/0003_index.sql", "BEGIN TRANSACTION;\nCREATE INDEX i ON o (Id);\nCOMMIT;");
        Write("up/0004_seed.TEST.UAT.ENV.sql", "INSERT c VALUES (1, 'a');");
        Write("runAfterCreateDatabase/rcsi.sql", "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON;");
        Write("views/vB.sql", "CREATE OR ALTER VIEW vB AS SELECT 1 AS x;");
        Write("views/everytime.audit.sql", "PRINT 'audit';");
        Write("sprocs/usp.sql", "CREATE OR ALTER PROCEDURE usp AS SELECT 1;");
        Write("ddl-functions/fn.sql", "CREATE OR ALTER FUNCTION fn() RETURNS int AS BEGIN RETURN 1 END;");
        Write("permissions/LOCAL.grants.env.sql", "GRANT SELECT ON vB TO [{{AppRole}}];");
        Write("dropDatabase/drop.sql", "x");
        Write("extra/notes.sql", "x");

        var imported = GrateImporter.Import(new GrateImportOptions
        {
            Path = dir,
            Folders = new Dictionary<string, string> { ["functions"] = "ddl-functions" },
            Tokens = new Dictionary<string, string> { ["approle"] = "reader" },
        });

        Assert.Equal(["rcsi", "0001-customer", "0002-orders", "sub-0003-index"], imported.Changes.Select(c => c.Name));
        Assert.Equal(["grate runAfterCreateDatabase/rcsi.sql", "grate up/0001_customer.sql", "grate up/0002_orders.sql", "grate up/sub/0003_index.sql"], imported.Changes.Select(c => c.Note));
        Assert.All(imported.Changes, c => Assert.True(c.Flags.HasFlag(ChangeFlags.Irreversible)));
        Assert.True(imported.Changes[3].Flags.HasFlag(ChangeFlags.NoTransaction));
        Assert.Equal("CREATE TABLE c (Id int, Tag nvarchar(9) DEFAULT '{{x}}'); -- {env} {grate.UpFolderName} {AppRole}", imported.Changes[1].Deploy);
        Assert.Equal("CREATE TABLE o (Id int); -- {db} on {db.host}", imported.Changes[2].Deploy);

        Assert.Equal(
            ["06-functions/fn.sql", "07-views/everytime.audit.sql", "07-views/vB.sql", "08-sprocs/usp.sql", "@env:LOCAL/12-permissions/LOCAL.grants.env.sql", "@env:TEST/04-up/0004_seed.TEST.UAT.ENV.sql", "@env:UAT/04-up/0004_seed.TEST.UAT.ENV.sql"],
            imported.Repeatables.Select(r => r.Path).Order(StringComparer.Ordinal));
        Assert.Equal("reader", imported.Variables["AppRole"]);
        Assert.Equal("up", imported.Variables["grate.UpFolderName"]);
        Assert.Equal(["LOCAL", "TEST", "UAT"], imported.Environments.Select(e => e.Name));

        var warnings = string.Join("\n", imported.Warnings);
        Assert.Contains("dropDatabase/: bsm does not create or drop databases", warnings, StringComparison.Ordinal);
        Assert.Contains("extra/: not a grate folder", warnings, StringComparison.Ordinal);
        Assert.Contains("everytime scripts", warnings, StringComparison.Ordinal);

        var project = WriteAndLoad(imported, "TEST");
        Assert.Empty(project.Problems);
        Assert.Equal(["06-functions/fn.sql", "07-views/everytime.audit.sql", "07-views/vB.sql", "08-sprocs/usp.sql", "@TEST/04-up/0004_seed.TEST.UAT.ENV.sql"], project.Repeatables.Select(r => r.Name));

        var explicitEnvironments = GrateImporter.Import(new GrateImportOptions { Path = dir, Environments = ["uat"] });
        Assert.Equal(["uat"], explicitEnvironments.Environments.Select(e => e.Name));
        Assert.Contains(explicitEnvironments.Warnings, w => w.Contains("LOCAL.grants.env.sql: no environment name recognised", StringComparison.Ordinal));
        Assert.Equal(["AppRole"], explicitEnvironments.UndefinedVariables);
    }

    [Fact]
    public void Grate_target_follows_scripts_run_and_checks_hashes()
    {
        Write("runAfterCreateDatabase/init.sql", "SELECT 0;");
        Write("up/a.sql", "SELECT 1;");
        Write("up/b.sql", "SELECT 2;");
        Write("up/c.sql", "SELECT 3;");
        Write("views/v.sql", "SELECT 4;");
        var project = WriteAndLoad(GrateImporter.Import(new GrateImportOptions { Path = dir }));
        string Hash(string text) => Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        GrateScriptRun Run(string name, string text, bool oneTime = true) => new(name, Hash(text), oneTime);
        static string Render(MigrationChange c) => c.DeployScript;

        Assert.Null(ForeignJournal.GrateTarget(project, [], Render));
        Assert.Equal("b", ForeignJournal.GrateTarget(project, [Run("a.sql", "SELECT 1;"), Run("b.sql", "SELECT 2;"), Run("v.sql", "SELECT 4;", false), Run("x.TEST.ENV.sql", "", true)], Render));
        Assert.Equal("a", ForeignJournal.GrateTarget(project, [Run("init.sql", "SELECT 0;", false), Run("a.sql", "SELECT 1;")], Render));

        var changed = Assert.Throws<MigrationException>(() => ForeignJournal.GrateTarget(project, [Run("a.sql", "SELECT 1; -- edited")], Render));
        Assert.Contains("grate recorded a different script", changed.Message, StringComparison.Ordinal);
        Assert.Equal("a", ForeignJournal.GrateTarget(project, [Run("a.sql", "SELECT 1; -- edited")], null));

        var gap = Assert.Throws<MigrationException>(() => ForeignJournal.GrateTarget(project, [Run("a.sql", "SELECT 1;"), Run("c.sql", "SELECT 3;")], Render));
        Assert.Contains("'b' (up/b.sql) was not run by grate", gap.Message, StringComparison.Ordinal);
        var unknown = Assert.Throws<MigrationException>(() => ForeignJournal.GrateTarget(project, [Run("z.sql", "SELECT 9;")], Render));
        Assert.Contains("grate ran the one-time script 'z.sql'", unknown.Message, StringComparison.Ordinal);

        Assert.Equal(["07-views/v.sql"], ForeignJournal.GrateRepeatables(project, [Run("v.sql", "SELECT 4;", false)], r => r.Script));
        Assert.Empty(ForeignJournal.GrateRepeatables(project, [Run("v.sql", "SELECT 5;", false)], r => r.Script));
    }

    [Fact]
    public void Flyway_repeatables_already_applied_are_found_by_checksum()
    {
        Write("V1__A.sql", "SELECT 1;");
        Write("R__View.sql", "CREATE VIEW ${s}.v AS SELECT 1;");
        var project = WriteAndLoad(FlywayImporter.Import(new FlywayImportOptions { Path = dir }));
        FlywayHistoryRow R(int rank, string text) => new(rank, null, "SQL", Crc32.Flyway(text), true, "R__View.sql");

        Assert.Equal(["View.sql"], ForeignJournal.FlywayRepeatables(project, [R(1, "old"), R(2, "CREATE VIEW app.v AS SELECT 1;")], r => r.Script.Replace("{s}", "app", StringComparison.Ordinal)));
        Assert.Empty(ForeignJournal.FlywayRepeatables(project, [R(1, "CREATE VIEW app.v AS SELECT 1;"), R(2, "newer")], r => r.Script.Replace("{s}", "app", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Baselined_repeatables_are_not_rerun()
    {
        var project = new TestProject();
        try
        {
            project.Change("a", "CREATE A", "DROP A").Repeatable("views/v.sql", "PRINT v");
            var db = new FakeDatabase();
            MigrationEngine Engine() => new(project.Load(), new FakeProvider(), new DeploymentEnvironment { Name = "test" }, new FakeSession(db), new Actor("t", "h", "1"));

            await Engine().BaselineAsync("a");
            Assert.Equal(["views/v.sql"], await Engine().BaselineRepeatablesAsync(new HashSet<string> { "views/v.sql", "missing.sql" }));
            Assert.Empty(await Engine().BaselineRepeatablesAsync(new HashSet<string> { "views/v.sql" }));

            var result = await Engine().DeployAsync(new DeployOptions());
            Assert.Empty(result.Repeatables);
            Assert.Empty(db.Executed);
        }
        finally
        {
            project.Dispose();
        }
    }

    // A plan written by Sqitch 1.6.1 (init, add, tag, rework, add).
    private const string SqitchPlan = """
        %syntax-version=1.0.0
        %project=flipr

        appschema 2026-11-03T20:56:05Z Ada Lovelace <ada@example.com> # Add schema for all flipr objects.
        users [appschema] 2026-11-03T20:56:06Z Ada Lovelace <ada@example.com> # Creates table to track our users.
        flips [appschema users] 2026-11-03T20:56:06Z Ada Lovelace <ada@example.com> # Adds table for storing flips.
        @v1.0.0 2026-11-03T20:56:06Z Ada Lovelace <ada@example.com> # Tag v1.0.0.
        users [users@v1.0.0] 2026-11-03T20:56:06Z Ada Lovelace <ada@example.com> # Add twitter column.
        notes [users@v1.0.0 flips !gone other:thing] 2026-11-03T20:56:07Z Ada Lovelace <ada@example.com> # Conflicts test.
        """;

    [Fact]
    public void Sqitch_project_with_rework_tags_and_dependencies()
    {
        Write("sqitch.conf", "[core]\n\tengine = pg\n[engine \"pg\"]\n\ttop_dir = db\n");
        Write("db/sqitch.plan", SqitchPlan);
        foreach (var (file, deploy) in new[] { ("appschema", "BEGIN;\nCREATE SCHEMA flipr;\nCOMMIT;"), ("users@v1.0.0", "CREATE TABLE users (nick TEXT);"), ("flips", "CREATE TABLE flips (id INT);"), ("users", "CREATE TABLE users (nick TEXT, twitter TEXT);"), ("notes", "SELECT '{note}';") })
        {
            Write($"db/deploy/{file}.sql", deploy);
            if (file != "notes")
            {
                Write($"db/revert/{file}.sql", $"-- revert {file}");
            }

            Write($"db/verify/{file}.sql", $"-- verify {file}");
        }

        var imported = SqitchImporter.Import(new SqitchImportOptions { Path = dir });

        Assert.Equal(["appschema", "users", "flips", "users-v2", "notes"], imported.Changes.Select(c => c.Name));
        Assert.Equal([[], ["appschema"], ["appschema", "users"], ["users"], ["users", "flips"]], imported.Changes.Select(c => c.Requires.ToArray()));
        Assert.Equal(["v1.0.0"], imported.Changes[2].Tags);
        Assert.Equal("CREATE TABLE users (nick TEXT);", imported.Changes[1].Deploy);
        Assert.Equal("-- revert users@v1.0.0", imported.Changes[1].Revert);
        Assert.Equal("CREATE TABLE users (nick TEXT, twitter TEXT);", imported.Changes[3].Deploy);
        Assert.Equal("-- verify users", imported.Changes[3].Verify);
        Assert.Equal("sqitch users#2: Add twitter column.", imported.Changes[3].Note);
        Assert.Equal(ChangeFlags.NoTransaction, imported.Changes[0].Flags);
        Assert.Equal(ChangeFlags.Irreversible, imported.Changes[4].Flags);
        Assert.Equal("SELECT '{{note}}';", imported.Changes[4].Deploy);
        Assert.Equal(("Ada_Lovelace", new DateTimeOffset(2026, 11, 3, 20, 56, 5, TimeSpan.Zero)), (imported.Changes[0].Author, imported.Changes[0].PlannedAt!.Value));
        Assert.False(imported.CanBaselineFromJournal);
        var warnings = string.Join("\n", imported.Warnings);
        Assert.Contains("conflicts with 'gone'", warnings, StringComparison.Ordinal);
        Assert.Contains("'other:thing' from another Sqitch project", warnings, StringComparison.Ordinal);
        Assert.Contains("copied as written for pg", warnings, StringComparison.Ordinal);

        var project = WriteAndLoad(imported);
        Assert.Empty(project.Problems);
        Assert.Equal(["users", "flips"], project.Changes[4].Plan.Requires);
        Assert.Equal(2, project.Plan.ResolveTarget("@v1.0.0"));
        Assert.Equal("-- verify users", project.Changes[3].VerifyScript);
    }

    [Fact]
    public void Sqitch_plan_errors_are_reported()
    {
        Write("sqitch.plan", "%project=x\n-gone 2026-11-03T20:56:05Z A <a@b> # reverted\n");
        Assert.Contains("'-' (revert) entries", Assert.Throws<MigrationException>(() => SqitchImporter.Import(new SqitchImportOptions { Path = dir })).Message, StringComparison.Ordinal);

        Write("sqitch.plan", "%project=x\na 2026-11-03T20:56:05Z A <a@b>\na 2026-11-03T20:56:06Z A <a@b>\n");
        Assert.Contains("reworked later but no tag follows it", Assert.Throws<MigrationException>(() => SqitchImporter.Import(new SqitchImportOptions { Path = dir })).Message, StringComparison.Ordinal);

        Write("sqitch.plan", "%project=x\nb 2026-11-03T20:56:05Z A <a@b>\n");
        Assert.Contains("has no deploy script", Assert.Throws<MigrationException>(() => SqitchImporter.Import(new SqitchImportOptions { Path = dir })).Message, StringComparison.Ordinal);
    }
}
