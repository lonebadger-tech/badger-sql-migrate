using System.IO.Compression;
using Badger.SqlMigrate.Core.Bundles;

namespace Badger.SqlMigrate.Core.Tests;

public sealed class BundleTests : IDisposable
{
    private readonly TestProject project = new();
    private readonly string work = Path.Combine(Path.GetTempPath(), "bsm-bundle-tests", Guid.NewGuid().ToString("N"));

    public BundleTests()
    {
        Directory.CreateDirectory(work);
        project.Change("a", "CREATE A", "DROP A", "CHECK A").Change("b [a]", "CREATE B", "DROP B").Repeatable("views/v.sql", "PRINT v");
        Directory.CreateDirectory(Path.Combine(project.Root, "environments", "dev"));
        project.Write("environments/dev/seed.sql", "PRINT seed");
        project.Write("bsm.yaml", "provider: fake\n");
    }

    public void Dispose()
    {
        project.Dispose();
        Directory.Delete(work, recursive: true);
    }

    private string Bundle(string name = "b.zip", bool config = true)
    {
        var path = Path.Combine(work, name);
        ProjectBundle.Create(project.Root, "fake", path, config, "1.0");
        return path;
    }

    [Fact]
    public void Bundle_round_trips_and_keeps_the_plan_head()
    {
        var path = Bundle();
        var manifest = ProjectBundle.Verify(path);

        Assert.Equal(project.Load().HeadChainId, manifest.PlanHead);
        Assert.Equal(2, manifest.Changes);
        Assert.True(manifest.IncludesConfig);
        Assert.Equal(
            ["bsm.plan", "bsm.yaml", "changes/a/deploy.sql", "changes/a/revert.sql", "changes/a/verify.sql", "changes/b/deploy.sql", "changes/b/revert.sql", "environments/dev/seed.sql", "repeatable/views/v.sql"],
            manifest.Files.Select(f => f.Path));

        var extracted = Path.Combine(work, "x");
        ProjectBundle.Extract(path, extracted);
        var loaded = Project.MigrationProject.Load(extracted, "fake", "dev");
        Assert.Equal(manifest.PlanHead, loaded.HeadChainId);
        Assert.Equal(["views/v.sql", "@dev/seed.sql"], loaded.Repeatables.Select(r => r.Name));
    }

    [Fact]
    public void Same_project_gives_byte_identical_bundles_whatever_the_line_endings()
    {
        project.Write("changes/a/deploy.sql", "-- a\nCREATE A\n");
        var first = File.ReadAllBytes(Bundle("1.zip"));
        project.Write("changes/a/deploy.sql", "\uFEFF-- a\r\nCREATE A\r\n");

        Assert.Equal(first, File.ReadAllBytes(Bundle("2.zip")));
    }

    [Fact]
    public void Config_can_be_left_out()
    {
        var manifest = ProjectBundle.Verify(Bundle(config: false));
        Assert.False(manifest.IncludesConfig);
        Assert.DoesNotContain(manifest.Files, f => f.Path == "bsm.yaml");
    }

    [Fact]
    public void Tampered_missing_or_extra_files_fail_verification()
    {
        var path = Bundle();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("changes/a/deploy.sql")!.Delete();
            using (var w = new StreamWriter(zip.CreateEntry("changes/a/deploy.sql").Open()))
            {
                w.Write("CREATE A; DROP TABLE Customers");
            }

            zip.GetEntry("changes/b/revert.sql")!.Delete();
            using (var w = new StreamWriter(zip.CreateEntry("changes/evil/deploy.sql").Open()))
            {
                w.Write("x");
            }
        }

        var ex = Assert.Throws<MigrationException>(() => ProjectBundle.Extract(path, Path.Combine(work, "x")));
        Assert.Contains("changes/a/deploy.sql has been modified", ex.Message, StringComparison.Ordinal);
        Assert.Contains("changes/b/revert.sql is listed in the manifest but missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("changes/evil/deploy.sql is not listed", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(work, "x")));
    }

    [Fact]
    public void A_project_with_problems_cannot_be_bundled()
    {
        project.Write("bsm.plan", "a\nb [a]\nmissing\n");
        var ex = Assert.Throws<MigrationException>(() => Bundle());
        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_a_bundle_is_a_clear_error()
    {
        var path = Path.Combine(work, "junk.zip");
        File.WriteAllText(path, "not a zip");
        Assert.Contains("is not a bsm bundle", Assert.Throws<MigrationException>(() => ProjectBundle.Verify(path)).Message, StringComparison.Ordinal);
    }
}
