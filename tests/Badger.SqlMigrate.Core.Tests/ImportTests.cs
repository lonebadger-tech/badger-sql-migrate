using Badger.SqlMigrate.Core.Hashing;
using Badger.SqlMigrate.Core.Import;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Project;

namespace Badger.SqlMigrate.Core.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "bsm-import-tests", Guid.NewGuid().ToString("N"));

    public ImportTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // Checksums recorded by Flyway 10.22 for these exact versioned files (repeatables are checksummed after placeholder replacement).
    [Theory]
    [InlineData("﻿ALTER TABLE ${appSchema}.Customer ADD Email nvarchar(200) NULL;\r\n-- created by ${flyway:user} in ${flyway:database} (${env})\r\n", 589044860)]
    [InlineData("CREATE INDEX IX_Customer_Email ON ${appSchema}.Customer (Email);\n", 1946864218)]
    public void Flyway_checksum_matches_flyway(string text, int expected) => Assert.Equal(expected, Crc32.Flyway(text));

    [Fact]
    public void Flyway_checksum_ignores_line_breaks_and_empty_lines_like_flyway()
    {
        Assert.Equal(Crc32.Flyway("a\nb\n"), Crc32.Flyway("a\r\nb"));
        Assert.Equal(Crc32.Flyway("a\nb"), Crc32.Flyway("a\rb\r\n"));
        Assert.Equal(Crc32.Flyway("a\nb"), Crc32.Flyway("a\n\nb"));
        Assert.NotEqual(Crc32.Flyway("a\nb"), Crc32.Flyway("a\nb "));
    }

    [Fact]
    public void Toml_and_conf_settings_are_flattened()
    {
        var toml = FlywayConfig.ParseToml("""
            # comment
            [flyway]
            locations = [
              "filesystem:sql",  # first
              'filesystem:more',
            ]
            "placeholderReplacement" = true

            [flyway.placeholders]
            a = "x # not a comment"
            b = "line\nbreak"

            [environments.prod]
            url = "jdbc:sqlserver://p;databaseName=App"
            """);
        Assert.Equal(["filesystem:sql", "filesystem:more"], toml.GetList("flyway.locations"));
        Assert.True(toml.GetBool("flyway.placeholderReplacement", false));
        Assert.Equal("x # not a comment", toml["flyway.placeholders.a"]);
        Assert.Equal("line\nbreak", toml["flyway.placeholders.b"]);
        Assert.Equal(["prod"], toml.EnvironmentNames);

        var conf = FlywayConfig.ParseConf("# c\nflyway.url=jdbc:sqlserver://h\nflyway.placeholders.x = 1\nflyway.locations=filesystem:a,\\\n  filesystem:b\n");
        Assert.Equal("1", conf["flyway.placeholders.x"]);
        Assert.Equal(["filesystem:a", "filesystem:b"], conf.GetList("flyway.locations"));
    }

    [Fact]
    public void Flyway_project_becomes_changes_reverts_repeatables_and_variables()
    {
        Write("flyway.conf", """
            flyway.locations=filesystem:sql
            flyway.defaultSchema=sales
            flyway.placeholders.schema=app
            flyway.placeholders.env=dev
            flyway.placeholders.unused=1
            flyway.url=jdbc:sqlserver://db.example.com:1444;databaseName=App;encrypt=true
            flyway.user=deployer
            flyway.password=secret
            """);
        Write("sql/V1__Init.sql", "CREATE SCHEMA ${schema};");
        Write("sql/U1__Init.sql", "DROP SCHEMA ${schema};");
        Write("sql/V1.9__Nine.sql", "SELECT '{x}', '{{y}}', $(z), '${env}', '${flyway:defaultSchema}', '${flyway:database}', '${missing}';");
        Write("sql/V1.10__Ten.sql", "CREATE INDEX i ON t (c);");
        Write("sql/V1.10__Ten.sql.conf", "executeInTransaction=false\nencoding=UTF-8\n");
        Write("sql/sub/V2__Init.sql", "SELECT 2;");
        Write("sql/R__Views.sql", "CREATE OR ALTER VIEW v AS SELECT 1 AS [${schema}];");
        Write("sql/B3__Baseline.sql", "SELECT 3;");
        Write("sql/afterMigrate.sql", "PRINT 1;");
        Write("sql/U9__Orphan.sql", "SELECT 9;");

        var imported = FlywayImporter.Import(new FlywayImportOptions { Path = dir });

        Assert.Equal(["init", "nine", "ten", "init-v2"], imported.Changes.Select(c => c.Name));
        var init = imported.Changes[0];
        Assert.Equal("CREATE SCHEMA {schema};", init.Deploy);
        Assert.Equal("DROP SCHEMA {schema};", init.Revert);
        Assert.Equal(ChangeFlags.None, init.Flags);
        Assert.Equal($"flyway V1 checksum {Crc32.Flyway("CREATE SCHEMA ${schema};")}", init.Note);
        Assert.Equal("SELECT '{{x}}', '{{y}}', $(z), '{flyway.env}', '{flyway.defaultSchema}', '{db}', '{missing}';", imported.Changes[1].Deploy);
        Assert.Equal(ChangeFlags.Irreversible, imported.Changes[1].Flags);
        Assert.Equal(ChangeFlags.Irreversible | ChangeFlags.NoTransaction, imported.Changes[2].Flags);
        Assert.Equal("flyway V2 checksum " + Crc32.Flyway("SELECT 2;"), imported.Changes[3].Note);

        var repeatable = Assert.Single(imported.Repeatables);
        Assert.Equal(("Views.sql", "CREATE OR ALTER VIEW v AS SELECT 1 AS [{schema}];"), (repeatable.Path, repeatable.Script));

        Assert.Equal("app", imported.Variables["schema"]);
        Assert.Equal("dev", imported.Variables["flyway.env"]);
        Assert.Equal("sales", imported.Variables["flyway.defaultSchema"]);
        Assert.False(imported.Variables.ContainsKey("unused"));
        Assert.Equal(["missing"], imported.UndefinedVariables);
        Assert.True(imported.DisableSqlCmdSyntax);

        var warnings = string.Join("\n", imported.Warnings);
        Assert.Contains("B3__Baseline.sql: baseline migrations are not imported", warnings, StringComparison.Ordinal);
        Assert.Contains("afterMigrate.sql", warnings, StringComparison.Ordinal);
        Assert.Contains("U9__Orphan.sql", warnings, StringComparison.Ordinal);
        Assert.Contains("'encoding' has no bsm equivalent", warnings, StringComparison.Ordinal);
        Assert.Contains("'{{name}}'", warnings, StringComparison.Ordinal);
        Assert.Contains("'unused' is defined but no script uses it", warnings, StringComparison.Ordinal);

        var environment = Assert.Single(imported.Environments);
        Assert.Equal("default", environment.Name);
        Assert.Equal("default", imported.DefaultEnvironment);
        Assert.Empty(environment.Settings);
        Assert.Contains("no connection mapping", warnings, StringComparison.Ordinal);
    }

    [Fact]
    public void Imported_project_writes_a_loadable_plan()
    {
        Write("sql/V1__A.sql", "SELECT 1;");
        Write("sql/U1__A.sql", "SELECT -1;");
        Write("sql/V2__B.sql", "SELECT 2;");
        var imported = FlywayImporter.Import(new FlywayImportOptions { Path = dir });
        var target = Path.Combine(dir, "out");

        imported.WriteScripts(target);
        var project = MigrationProject.Load(target, "fake");

        Assert.Empty(project.Problems);
        Assert.Equal(["a", "b"], project.Changes.Select(c => c.Name));
        Assert.Equal("SELECT -1;", project.Changes[0].RevertScript);
        Assert.True(project.Changes[1].Plan.Flags.HasFlag(ChangeFlags.Irreversible));
        Assert.StartsWith("flyway V2 checksum", project.Changes[1].Plan.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_prefixes_placeholder_syntax_and_toml_environment_placeholders()
    {
        Write("flyway.toml", """
            [flyway]
            sqlMigrationPrefix = "M"
            sqlMigrationSeparator = "-"
            sqlMigrationSuffixes = [".ddl"]
            placeholderPrefix = "@@"
            placeholderSuffix = "@@"

            [flyway.placeholders]
            role = "reader"

            [environments.test]
            url = "jdbc:sqlserver://t;databaseName=T"
            [environments.test.flyway.placeholders]
            role = "test_reader"
            """);
        Write("M1-Grant.ddl", "GRANT SELECT TO [@@role@@]; -- ${not a placeholder}");
        Write("V2__Ignored.sql", "SELECT 1;");

        var imported = FlywayImporter.Import(new FlywayImportOptions { Path = dir, MapConnection = (c, _) => new() { ["host"] = c.Url } });

        var change = Assert.Single(imported.Changes);
        Assert.Equal("GRANT SELECT TO [{role}]; -- ${not a placeholder}", change.Deploy);
        Assert.Equal("reader", imported.Variables["role"]);
        var test = Assert.Single(imported.Environments);
        Assert.Equal("jdbc:sqlserver://t;databaseName=T", (string?)test.Settings["host"]);
        Assert.Equal("test_reader", (string?)test.Settings["variables"]!["role"]);
    }

    [Fact]
    public void Duplicate_flyway_versions_are_an_error()
    {
        Write("V1__A.sql", "1");
        Write("V1.0__B.sql", "2");
        Assert.Contains("used by both", Assert.Throws<MigrationException>(() => FlywayImporter.Import(new FlywayImportOptions { Path = dir })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Flyway_target_follows_history_including_undo_baseline_and_gaps()
    {
        Write("V1__A.sql", "1");
        Write("V2__B.sql", "2");
        Write("V3__C.sql", "3");
        var imported = FlywayImporter.Import(new FlywayImportOptions { Path = dir });
        var target = Path.Combine(dir, "out");
        imported.WriteScripts(target);
        var project = MigrationProject.Load(target, "fake");
        FlywayHistoryRow Row(int rank, string? version, string type = "SQL", int? checksum = null, bool success = true) =>
            new(rank, version, type, checksum ?? (version is null ? 0 : Crc32.Flyway(version)), success, $"V{version}__x.sql");

        Assert.Null(ForeignJournal.FlywayTarget(project, [], true));
        Assert.Equal("b", ForeignJournal.FlywayTarget(project, [Row(1, "1"), Row(2, "2"), Row(3, "3", success: false)], true));
        Assert.Equal("a", ForeignJournal.FlywayTarget(project, [Row(1, "1"), Row(2, "2"), Row(3, "2", "UNDO_SQL")], true));
        Assert.Equal("b", ForeignJournal.FlywayTarget(project, [Row(1, "2", "BASELINE", checksum: 0)], true));
        Assert.Equal("c", ForeignJournal.FlywayTarget(project, [Row(1, "1"), Row(2, null, checksum: 5), Row(3, "2"), Row(4, "3")], true));

        var gap = Assert.Throws<MigrationException>(() => ForeignJournal.FlywayTarget(project, [Row(1, "1"), Row(2, "3")], true));
        Assert.Contains("'b' (V2) was not applied by Flyway, but later versions were", gap.Message, StringComparison.Ordinal);

        var checksum = Assert.Throws<MigrationException>(() => ForeignJournal.FlywayTarget(project, [Row(1, "1", checksum: 42)], true));
        Assert.Contains("recorded checksum 42", checksum.Message, StringComparison.Ordinal);
        Assert.Equal("a", ForeignJournal.FlywayTarget(project, [Row(1, "1", checksum: 42)], false));

        var unknown = Assert.Throws<MigrationException>(() => ForeignJournal.FlywayTarget(project, [Row(1, "1"), Row(2, "7")], true));
        Assert.Contains("Flyway applied V7", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DbUp_scripts_import_in_ordinal_name_order_with_variables_and_run_always()
    {
        Write("Script0001 - Schema.sql", "CREATE SCHEMA [$schema$];");
        Write("V2/Script0005 - a.sql", "SELECT 'a', '{literal}';");
        Write("V2/Script0005 - B.sql", "SELECT 'B', $other$;");
        Write("Always/Grants.sql", "GRANT SELECT ON SCHEMA::[$schema$] TO r;");

        var imported = DbUpImporter.Import(new DbUpImportOptions
        {
            Path = dir,
            RunAlways = ["Always/**"],
            Variables = new Dictionary<string, string> { ["schema"] = "app" },
        });

        Assert.Equal(["script0001-schema", "v2.script0005-b", "v2.script0005-a"], imported.Changes.Select(c => c.Name));
        Assert.Equal(["dbup Script0001 - Schema.sql", "dbup V2.Script0005 - B.sql", "dbup V2.Script0005 - a.sql"], imported.Changes.Select(c => c.Note));
        Assert.All(imported.Changes, c => Assert.Equal(ChangeFlags.Irreversible, c.Flags));
        Assert.Equal("CREATE SCHEMA [{schema}];", imported.Changes[0].Deploy);
        Assert.Equal("SELECT 'a', '{{literal}}';", imported.Changes[2].Deploy);
        Assert.Equal("app", imported.Variables["schema"]);
        Assert.Equal(["other"], imported.UndefinedVariables);
        Assert.Equal(("Always/Grants.sql", "GRANT SELECT ON SCHEMA::[{schema}] TO r;"), (imported.Repeatables.Single().Path, imported.Repeatables.Single().Script));

        var names = DbUpImporter.Import(new DbUpImportOptions { Path = dir, FileNamesOnly = true, VariablesDisabled = true });
        Assert.Equal(["dbup Script0001 - Schema.sql"], names.Changes.Select(c => c.Note));
        Assert.Equal("CREATE SCHEMA [$schema$];", names.Changes[0].Deploy);
    }

    [Fact]
    public void DbUp_target_matches_embedded_resource_names_by_suffix()
    {
        Write("Script0001 - A.sql", "1");
        Write("Sub/Script0002 - B.sql", "2");
        Write("Sub/Script0003 - C.sql", "3");
        Write("Always/G.sql", "g");
        var target = Path.Combine(dir, "out");
        DbUpImporter.Import(new DbUpImportOptions { Path = dir, RunAlways = ["Always/*.sql"] }).WriteScripts(target);
        var project = MigrationProject.Load(target, "fake");

        Assert.Null(ForeignJournal.DbUpTarget(project, []));
        Assert.Equal("sub.script0002-b", ForeignJournal.DbUpTarget(project, ["App.Scripts.Script0001 - A.sql", "App.Scripts.Sub.Script0002 - B.sql", "App.Scripts.Always.G.sql"]));

        var gap = Assert.Throws<MigrationException>(() => ForeignJournal.DbUpTarget(project, ["App.Scripts.Script0001 - A.sql", "App.Scripts.Sub.Script0003 - C.sql"]));
        Assert.Contains("'sub.script0002-b' (Sub.Script0002 - B.sql) was not run by DbUp", gap.Message, StringComparison.Ordinal);
        var unknown = Assert.Throws<MigrationException>(() => ForeignJournal.DbUpTarget(project, ["App.Scripts.Script0009 - Z.sql"]));
        Assert.Contains("DbUp ran 'App.Scripts.Script0009 - Z.sql'", unknown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("schema", "schema")]
    [InlineData("env", "flyway.env")]
    [InlineData("db.name", "flyway.db.name")]
    [InlineData("my-var", "my_var")]
    [InlineData("1st", "_1st")]
    public void Source_variable_names_become_valid_bsm_names(string name, string expected) => Assert.Equal(expected, ImportedProject.VariableName(name, "flyway"));
}
