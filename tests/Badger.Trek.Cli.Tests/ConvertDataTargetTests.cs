using Badger.Trek.Cli;
using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;

namespace Badger.Trek.Cli.Tests;

/// <summary>
/// 'trek convert data' runs from the source project, but the SQLite target may have been deployed from its own folder:
/// a relative 'database:' must find the file that was deployed, never fill or create a different one.
/// </summary>
public sealed class ConvertDataTargetTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "trek-convert-target", Guid.NewGuid().ToString("N"));

    public ConvertDataTargetTests() => Directory.CreateDirectory(Path.Combine(work, "converted"));

    public void Dispose() => Directory.Delete(work, true);

    private static DeploymentEnvironment Env(string database) => new() { Name = "local", Database = database };

    private string Create(params string[] parts)
    {
        var path = Path.Combine([work, .. parts]);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void Deployed_from_the_target_folder_is_found_from_the_source_folder()
    {
        var deployed = Create("converted", "app.db");

        var env = Commands.SqliteTargetFile(Env("app.db"), "converted", work);

        Assert.Equal(deployed, env.Database);
    }

    [Fact]
    public void Deployed_with_dash_C_from_here_is_used_as_before()
    {
        var deployed = Create("app.db");

        Assert.Equal(deployed, Commands.SqliteTargetFile(Env("app.db"), "converted", work).Database);
    }

    [Fact]
    public void Two_candidate_files_stop_with_both_paths()
    {
        Create("app.db");
        Create("converted", "app.db");

        var ex = Assert.Throws<MigrationException>(() => Commands.SqliteTargetFile(Env("app.db"), "converted", work));

        Assert.Contains(Path.Combine(work, "app.db"), ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(work, "converted", "app.db"), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_deployed_file_says_to_deploy_first_instead_of_creating_one()
    {
        var ex = Assert.Throws<MigrationException>(() => Commands.SqliteTargetFile(Env("app.db"), "converted", work));

        Assert.Contains("Deploy the converted project first (trek deploy -C converted)", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(work, "app.db")));
        Assert.False(File.Exists(Path.Combine(work, "converted", "app.db")));
    }

    [Fact]
    public void Absolute_and_special_paths_are_left_alone()
    {
        var absolute = Path.Combine(work, "elsewhere.db");
        Assert.Equal(absolute, Commands.SqliteTargetFile(Env(absolute), "converted", work).Database);
        Assert.Equal("~/app.db", Commands.SqliteTargetFile(Env("~/app.db"), "converted", work).Database);
        var withConnectionString = new DeploymentEnvironment { Name = "local", Database = "app.db", ConnectionString = "Data Source=x.db" };
        Assert.Same(withConnectionString, Commands.SqliteTargetFile(withConnectionString, "converted", work));
    }

    [Fact]
    public void Running_from_the_target_folder_itself_uses_that_file()
    {
        var deployed = Create("converted", "app.db");

        Assert.Equal(deployed, Commands.SqliteTargetFile(Env("app.db"), ".", Path.Combine(work, "converted")).Database);
        File.Delete(deployed);
        Assert.Throws<MigrationException>(() => Commands.SqliteTargetFile(Env("app.db"), ".", Path.Combine(work, "converted")));
    }
}
