using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Preprocessing;

namespace Badger.SqlMigrate.Core.Tests;

public sealed class PreprocessorTests : IDisposable
{
    private static readonly ScriptContext Ctx = new("add-calc", "deploy", "abc123");
    private readonly TestProject project = new();

    public void Dispose() => project.Dispose();

    private static ScriptPreprocessor Pre(Dictionary<string, string>? user = null, UnknownVariablePolicy unknown = UnknownVariablePolicy.Error, string env = "test") =>
        new(
            ScriptPreprocessor.BuiltIns(env, "Test env", "AppDb", "sql.example.com", 1433, "sqlserver", "1.2.3", "badger", "head", "brock", "mac", new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero), "run1"),
            (user ?? []).Select(kv => new VariableInfo(kv.Key, kv.Value, VariableSource.Config)),
            new PreprocessorOptions { Unknown = unknown });

    [Fact]
    public void Expands_the_env_example_from_the_request()
    {
        Assert.Equal("select * from [calc_test].[table1]", Pre().Render("select * from [calc_{env}].[table1]", Ctx).Text);
    }

    [Theory]
    [InlineData("{env.name}", "test")]
    [InlineData("{env.description}", "Test env")]
    [InlineData("{db}", "AppDb")]
    [InlineData("{db.host}:{db.port}", "sql.example.com:1433")]
    [InlineData("{badger.provider} {badger.version} {badger.schema}", "sqlserver 1.2.3 badger")]
    [InlineData("{change} {change.kind} {change.hash}", "add-calc deploy abc123")]
    [InlineData("{deploy.utc} {deploy.date}", "2026-11-03T18:00:00Z 2026-11-03")]
    [InlineData("$(env)", "test")]
    public void Built_ins(string script, string expected) => Assert.Equal(expected, Pre().Render(script, Ctx).Text);

    [Fact]
    public void User_variables_nest_and_reference_each_other()
    {
        var pre = Pre(new() { ["schemas.calc"] = "calc_{env}", ["schemas.stage"] = "stage_{env}", ["calcTable"] = "[{schemas.calc}].[table1]", ["AppRole"] = "app_user" });

        Assert.Equal("select * from [calc_test].[table1]; GRANT SELECT TO app_user; stage_test",
            pre.Render("select * from {calcTable}; GRANT SELECT TO {var.AppRole}; $(schemas.stage)", Ctx).Text);
    }

    [Fact]
    public void Names_are_case_insensitive() => Assert.Equal("test app", Pre(new() { ["AppRole"] = "app" }).Render("{ENV} {approle}", Ctx).Text);

    [Theory]
    [InlineData("SELECT {fn NOW()}")]
    [InlineData("SELECT {d '2026-01-01'}")]
    [InlineData("SELECT '{\"a\":{\"b\":1}}'")]
    [InlineData("SELECT FORMATMESSAGE('{0} and {1}')")]
    [InlineData("SELECT '{ env }'")]
    public void Sql_that_merely_contains_braces_is_untouched(string sql) => Assert.Equal(sql, Pre().Render(sql, Ctx).Text);

    [Fact]
    public void Double_braces_escape() => Assert.Equal("SELECT '{env}' AS literal, 'test' AS value", Pre().Render("SELECT '{{env}}' AS literal, '{env}' AS value", Ctx).Text);

    [Fact]
    public void Unknown_variables_are_errors_with_line_numbers()
    {
        var ex = Assert.Throws<MigrationException>(() => Pre().Render("SELECT 1;\nSELECT * FROM [{tenant}].t;", Ctx));
        Assert.Contains("line 2: unknown variable {tenant}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("add-calc (deploy)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keep_policy_leaves_unknown_braces_but_never_unknown_builtins()
    {
        var pre = Pre(unknown: UnknownVariablePolicy.Keep);
        Assert.Equal("SELECT '{whatever}', test", pre.Render("SELECT '{whatever}', {env}", Ctx).Text);
        Assert.Throws<MigrationException>(() => pre.Render("{env.nmae}", Ctx));
    }

    [Fact]
    public void Preprocessing_can_be_turned_off_per_script()
    {
        const string script = "-- badger:preprocess off\nSELECT '{env}', '$(x)'";
        Assert.Equal(script, Pre().Render(script, Ctx).Text);
    }

    [Fact]
    public void Cycles_are_reported() =>
        Assert.Contains("cycle", Assert.Throws<MigrationException>(() => Pre(new() { ["a"] = "{b}", ["b"] = "{a}" }).Render("{a}", Ctx)).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("env")]
    [InlineData("db.name")]
    [InlineData("deploy.whatever")]
    [InlineData("change")]
    public void Built_in_names_are_reserved(string name) =>
        Assert.Throws<MigrationException>(() => Pre(new() { [name] = "x" }));

    [Fact]
    public void Per_run_values_are_flagged_volatile()
    {
        Assert.True(Pre().Render("-- {deploy.utc}", Ctx).UsesVolatile);
        Assert.False(Pre(new() { ["x"] = "{env}" }).Render("{x}", Ctx).UsesVolatile);
        Assert.True(Pre(new() { ["stamp"] = "{deploy.id}" }).Render("{stamp}", Ctx).UsesVolatile);
    }

    [Fact]
    public void Find_references_reports_names_and_lines_but_not_escapes()
    {
        var refs = ScriptPreprocessor.FindReferences("SELECT {env};\nSELECT $(Region), '{{literal}}', {schemas.calc}");
        Assert.Equal(["env", "Region", "schemas.calc"], refs.Select(r => r.Name));
        Assert.Equal([1, 2, 2], refs.Select(r => r.Line));
    }

    [Fact]
    public async Task Engine_renders_scripts_and_records_rendered_hashes()
    {
        project.Change("a", "CREATE calc_{env}", "DROP calc_{env}");
        var db = new FakeDatabase();
        var engine = new MigrationEngine(project.Load(), new FakeProvider(), new DeploymentEnvironment { Name = "test" }, new FakeSession(db), new Actor("u", "h", "1"));

        await engine.DeployAsync(new DeployOptions());

        Assert.Contains("calc_test", db.Objects);
        Assert.NotNull(db.Changes[0].RenderedHash);
        Assert.NotNull(db.Changes[0].RenderedRevertHash);
        Assert.Equal("CREATE calc_{env}", db.Changes[0].DeployScript);
    }

    [Fact]
    public async Task Same_raw_scripts_render_differently_per_environment_but_certify_identically()
    {
        project.Change("a", "CREATE calc_{env}", "DROP calc_{env}");
        var dbs = new Dictionary<string, FakeDatabase> { ["test"] = new(), ["prod"] = new() };
        foreach (var (env, db) in dbs)
        {
            await new MigrationEngine(project.Load(), new FakeProvider(), new DeploymentEnvironment { Name = env }, new FakeSession(db), new Actor("u", "h", "1")).DeployAsync(new DeployOptions());
        }

        Assert.Equal(dbs["test"].Changes[0].ChainId, dbs["prod"].Changes[0].ChainId);
        Assert.NotEqual(dbs["test"].Changes[0].RenderedHash, dbs["prod"].Changes[0].RenderedHash);
        Assert.Contains("calc_prod", dbs["prod"].Objects);
    }

    [Fact]
    public async Task Changed_variable_values_are_caught_by_verify_and_revert()
    {
        project.Change("a", "CREATE {target}", "DROP {target}");
        var db = new FakeDatabase();
        MigrationEngine Engine(string target) => new(project.Load(), new FakeProvider(), new DeploymentEnvironment { Name = "test", Variables = new Dictionary<string, string> { ["target"] = target } }, new FakeSession(db), new Actor("u", "h", "1"));

        await Engine("T1").DeployAsync(new DeployOptions());
        Assert.True((await Engine("T1").VerifyAsync()).Ok);

        var verify = await Engine("T2").VerifyAsync();
        Assert.Contains(verify.Problems, p => p.Contains("renders differently", StringComparison.Ordinal));

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Engine("T2").RevertAsync(new RevertOptions { All = true }));
        Assert.Contains("--allow-variable-drift", ex.Message, StringComparison.Ordinal);
        Assert.Contains("T1", db.Objects);

        await Engine("T1").RevertAsync(new RevertOptions { All = true });
        Assert.Empty(db.Objects);
    }

    [Fact]
    public async Task Command_line_variables_override_config()
    {
        project.Change("a", "CREATE {target}", "DROP {target}");
        var db = new FakeDatabase();
        var engine = new MigrationEngine(project.Load(), new FakeProvider(), new DeploymentEnvironment { Name = "test", Variables = new Dictionary<string, string> { ["target"] = "FromConfig" } }, new FakeSession(db), new Actor("u", "h", "1"), variableOverrides: new Dictionary<string, string> { ["target"] = "FromCli" });

        await engine.DeployAsync(new DeployOptions());

        Assert.Equal(["FromCli"], db.Objects);
        Assert.Equal(VariableSource.CommandLine, engine.Preprocessor.Describe().Single(v => v.Name == "target").Source);
    }
}
