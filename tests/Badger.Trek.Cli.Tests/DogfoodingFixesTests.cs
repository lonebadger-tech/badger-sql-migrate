using Badger.Trek.Core.Plan;

namespace Badger.Trek.Cli.Tests;

/// <summary>Rough edges found while using trek on a real project.</summary>
public sealed class DogfoodingFixesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "trek-dogfooding", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GlobalOptions G(string dir, string? environment = null) => new(dir, environment, null, [], false, new ConsoleOutput(false, false, true)) { AgentsFile = false };

    [Fact]
    public void Add_takes_an_author()
    {
        Assert.Equal(0, Commands.Init(G(root), "postgresql"));
        Assert.Equal(0, Commands.Add(G(root), "create-thing", [], null, false, false, author: "Ada Lovelace"));

        var change = PlanFile.Load(Path.Combine(root, PlanFile.FileName)).Changes.Single();
        Assert.Equal("Ada_Lovelace", change.Author);
    }

    [Fact]
    public void The_default_author_is_TREK_AUTHOR_then_git_user_name()
    {
        var previous = Environment.GetEnvironmentVariable("TREK_AUTHOR");
        try
        {
            Environment.SetEnvironmentVariable("TREK_AUTHOR", "pipeline bot");
            Assert.Equal("pipeline_bot", Commands.Author(root));
            Assert.Equal("explicit", Commands.Author(root, "explicit"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TREK_AUTHOR", previous);
        }
    }

    [Fact]
    public void Env_show_and_vars_work_when_an_env_reference_is_unset()
    {
        Directory.CreateDirectory(root);
        Assert.Equal(0, Commands.Init(G(root), "postgresql"));
        File.WriteAllText(Path.Combine(root, "trek.yaml"), """
            provider: postgresql
            defaultEnvironment: ci
            variables:
              apiKey: ${env:TREK_TEST_UNSET_6f1e}
            environments:
              ci:
                host: db.example.test
                database: app
                authentication: password
                user: deploy
                password: ${env:TREK_TEST_UNSET_6f1e}
            """);

        Assert.Equal(0, Commands.EnvShow(G(root)));
        Assert.Equal(0, Commands.Vars(G(root)));
    }
}
