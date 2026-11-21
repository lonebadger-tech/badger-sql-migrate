namespace Badger.Trek.Cli.Tests;

/// <summary>Every provider's 'trek init' template loads, passes plan check, and its local environment is valid.</summary>
public sealed class InitTemplateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "trek-init-templates", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GlobalOptions G(string dir) => new(dir, null, null, [], false, new ConsoleOutput(false, false, true)) { AgentsFile = false };

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("oracle")]
    [InlineData("sqlite")]
    public void The_template_is_a_valid_project(string provider)
    {
        var dir = Path.Combine(root, provider);
        Assert.Equal(0, Commands.Init(G(dir), provider));
        Assert.Equal(0, Commands.PlanCheck(G(dir)));

        var config = TrekConfig.Load(dir);
        var created = config.CreateProvider();
        Assert.Equal(provider, created.Id);
        var environment = config.ResolveEnvironment(null, null, keepUnresolved: true);
        Assert.Empty(created.ValidateEnvironment(environment));
    }
}
