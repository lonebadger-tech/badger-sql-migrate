using Badger.Trek.Core.Import;
using Badger.Trek.Core.Plan;
using Badger.Trek.Core.Project;

namespace Badger.Trek.Core.Tests;

/// <summary>
/// Entity Framework Core 10 migrations, imported from the scripts 'dotnet ef migrations script' wrote for SQL Server,
/// PostgreSQL and SQLite (EfScripts/): three migrations, Initial, AddBlogUrl and RatingDropTitle.
/// </summary>
public sealed class EfImportTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "trek-ef-tests", Guid.NewGuid().ToString("N"));

    public EfImportTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static string Script(string provider, string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "EfScripts", provider, name + ".sql"));

    private static ImportedProject Import(string provider, bool withDown = true) => EfImporter.Import(new EfImportOptions
    {
        UpScript = Script(provider, "up"),
        DownScript = withDown ? Script(provider, "down") : null,
    });

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    [InlineData("sqlite")]
    public void Each_migration_becomes_a_change_with_its_down_as_revert(string provider)
    {
        Assert.Equal(provider, EfImporter.DetectProvider(Script(provider, "up")));
        var imported = Import(provider);

        Assert.Equal(["initial", "add-blog-url", "rating-drop-title"], imported.Changes.Select(c => c.Name));
        Assert.All(imported.Changes, c =>
        {
            Assert.Matches(@"^ef 2026\d{10}_(Initial|AddBlogUrl|RatingDropTitle)$", c.Note);
            Assert.NotNull(c.Revert);
            Assert.False(c.Flags.HasFlag(ChangeFlags.Irreversible));
            Assert.NotNull(c.PlannedAt);
            foreach (var sql in new[] { c.Deploy, c.Revert! })
            {
                // trek runs each change in its own transaction and keeps its own journal.
                Assert.DoesNotContain("__EFMigrationsHistory", sql, StringComparison.Ordinal);
                Assert.DoesNotMatch(@"(?im)^\s*(BEGIN TRANSACTION|START TRANSACTION|COMMIT)\s*;", sql);
                Assert.DoesNotMatch(@"(?i)^\s*GO\s*$|\n\s*GO\s*$", sql.Trim());
                Assert.False(sql.StartsWith('﻿'));
            }
        });
        Assert.Contains("CREATE TABLE", imported.Changes[0].Deploy, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE", imported.Changes[0].Revert!, StringComparison.Ordinal);
        Assert.Contains("Url", imported.Changes[1].Deploy, StringComparison.Ordinal);
        Assert.Contains("\"Title\"", imported.Changes[2].Revert!.Replace("[Title]", "\"Title\"", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Sqlite_table_rebuilds_with_foreign_keys_off_are_notx()
    {
        var imported = Import("sqlite");

        // AddBlogUrl's Down() and RatingDropTitle's Up() and Down() rebuild a table.
        Assert.Equal([false, true, true], imported.Changes.Select(c => c.Flags.HasFlag(ChangeFlags.NoTransaction)));
        Assert.Contains("PRAGMA foreign_keys = 0;", imported.Changes[2].Deploy, StringComparison.Ordinal);
        Assert.Equal(2, imported.Warnings.Count(w => w.Contains("!notx", StringComparison.Ordinal)));
    }

    [Fact]
    public void Without_a_down_script_every_change_is_irreversible()
    {
        var imported = Import("postgresql", withDown: false);

        Assert.All(imported.Changes, c => Assert.True(c.Flags.HasFlag(ChangeFlags.Irreversible)));
        Assert.All(imported.Changes, c => Assert.Null(c.Revert));
        Assert.Contains(imported.Warnings, w => w.Contains("dotnet ef migrations script <last migration> 0", StringComparison.Ordinal));
    }

    [Fact]
    public void An_idempotent_script_is_refused()
    {
        var idempotent = """
            IF NOT EXISTS (
                SELECT * FROM [__EFMigrationsHistory]
                WHERE [MigrationId] = N'20261120131330_Initial'
            )
            BEGIN
                CREATE TABLE [Blogs] ([Id] int NOT NULL IDENTITY);
            END;
            """;

        var ex = Assert.Throws<MigrationException>(() => EfImporter.Import(new EfImportOptions { UpScript = idempotent }));
        Assert.Contains("--idempotent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_without_migrations_is_refused()
    {
        Assert.Throws<MigrationException>(() => EfImporter.Import(new EfImportOptions { UpScript = "SELECT 1;" }));
    }

    [Fact]
    public void Baseline_target_follows_ef_migrations_history()
    {
        var imported = Import("sqlserver");
        imported.WriteScripts(dir);
        var project = MigrationProject.Load(dir, "fake");
        var ids = imported.Changes.Select(c => c.Note[3..]).ToList();

        Assert.Null(ForeignJournal.EfTarget(project, []));
        Assert.Equal("add-blog-url", ForeignJournal.EfTarget(project, ids.Take(2).ToList()));
        Assert.Equal("rating-drop-title", ForeignJournal.EfTarget(project, ids));

        var gap = Assert.Throws<MigrationException>(() => ForeignJournal.EfTarget(project, [ids[0], ids[2]]));
        Assert.Contains("'add-blog-url'", gap.Message, StringComparison.Ordinal);

        var unknown = Assert.Throws<MigrationException>(() => ForeignJournal.EfTarget(project, [.. ids, "20261201000000_Later"]));
        Assert.Contains("20261201000000_Later", unknown.Message, StringComparison.Ordinal);
    }
}
