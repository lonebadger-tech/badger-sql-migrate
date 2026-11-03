using System.Text;
using Badger.SqlMigrate.Core.Project;

namespace Badger.SqlMigrate.Core.Tests;

/// <summary>Builds a throwaway project directory on disk.</summary>
public sealed class TestProject : IDisposable
{
    public TestProject()
    {
        Root = Path.Combine(Path.GetTempPath(), "bsm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, MigrationProject.ChangesFolder));
        File.WriteAllText(Path.Combine(Root, "bsm.plan"), string.Empty);
    }

    public string Root { get; }

    public TestProject Change(string planLine, string deploy, string? revert = null, string? verify = null)
    {
        var name = planLine.Split(' ')[0];
        var dir = Path.Combine(Root, MigrationProject.ChangesFolder, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MigrationProject.DeployFile), deploy, Encoding.UTF8);
        if (revert is not null)
        {
            File.WriteAllText(Path.Combine(dir, MigrationProject.RevertFile), revert, Encoding.UTF8);
        }

        if (verify is not null)
        {
            File.WriteAllText(Path.Combine(dir, MigrationProject.VerifyFile), verify, Encoding.UTF8);
        }

        File.AppendAllText(Path.Combine(Root, "bsm.plan"), planLine + "\n");
        return this;
    }

    public TestProject Repeatable(string name, string script)
    {
        var path = Path.Combine(Root, MigrationProject.RepeatableFolder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, script);
        return this;
    }

    public void WritePlan(string text) => File.WriteAllText(Path.Combine(Root, "bsm.plan"), text);

    public void Write(string relativePath, string text) => File.WriteAllText(Path.Combine(Root, relativePath), text);

    public void DeleteChange(string name) => Directory.Delete(Path.Combine(Root, MigrationProject.ChangesFolder, name), recursive: true);

    public MigrationProject Load(string provider = "fake", string? environment = null) => MigrationProject.Load(Root, provider, environment);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
