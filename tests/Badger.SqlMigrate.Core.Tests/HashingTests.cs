using System.Text;
using Badger.SqlMigrate.Core.Hashing;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Project;

namespace Badger.SqlMigrate.Core.Tests;

public class HashingTests
{
    private static readonly PlanChange Change = new("c", [], ChangeFlags.None, null, null, null, 1);

    [Fact]
    public void Crlf_lf_and_bom_hash_identically()
    {
        var lf = MigrationChange.ComputeScriptHash(Change, ContentHasher.Normalize("CREATE TABLE t (id int);\nGO\n"), null, null);
        var crlf = MigrationChange.ComputeScriptHash(Change, ContentHasher.Normalize("CREATE TABLE t (id int);\r\nGO\r\n"), null, null);
        var bom = MigrationChange.ComputeScriptHash(Change, ContentHasher.Normalize("﻿CREATE TABLE t (id int);\nGO\n"), null, null);

        Assert.Equal(lf, crlf);
        Assert.Equal(lf, bom);
        Assert.Equal(64, lf.Length);
    }

    [Fact]
    public void Files_saved_with_crlf_and_bom_on_windows_hash_like_lf_files_on_mac()
    {
        using var mac = new TestProject();
        using var windows = new TestProject();
        mac.Change("a", "CREATE x\nGO\n", "DROP x\n");
        windows.Change("a", "x");
        File.WriteAllText(Path.Combine(windows.Root, "changes", "a", "deploy.sql"), "CREATE x\r\nGO\r\n", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(windows.Root, "changes", "a", "revert.sql"), "DROP x\r\n", new UTF8Encoding(true));

        Assert.Equal(mac.Load().HeadChainId, windows.Load().HeadChainId);
    }

    [Fact]
    public void Any_meaningful_edit_changes_the_hash()
    {
        var original = MigrationChange.ComputeScriptHash(Change, "CREATE x", "DROP x", null);

        Assert.NotEqual(original, MigrationChange.ComputeScriptHash(Change, "CREATE x ", "DROP x", null));
        Assert.NotEqual(original, MigrationChange.ComputeScriptHash(Change, "CREATE x", "DROP x", ""));
        Assert.NotEqual(original, MigrationChange.ComputeScriptHash(Change with { Flags = ChangeFlags.NoTransaction }, "CREATE x", "DROP x", null));
        Assert.NotEqual(original, MigrationChange.ComputeScriptHash(Change with { Requires = ["b"] }, "CREATE x", "DROP x", null));

        // Framing: text moved between deploy and revert must not collide.
        Assert.NotEqual(
            MigrationChange.ComputeScriptHash(Change, "ab", "c", null),
            MigrationChange.ComputeScriptHash(Change, "a", "bc", null));
    }

    [Fact]
    public void Plan_notes_authors_and_timestamps_do_not_affect_the_hash()
    {
        var annotated = Change with { Note = "note", Author = "alice", PlannedAt = DateTimeOffset.UnixEpoch };
        Assert.Equal(
            MigrationChange.ComputeScriptHash(Change, "CREATE x", "DROP x", null),
            MigrationChange.ComputeScriptHash(annotated, "CREATE x", "DROP x", null));
    }

    [Fact]
    public void Chain_id_depends_on_order_and_provider()
    {
        var a = MigrationChange.ComputeChainId("sqlserver", "a", "h1", null);
        var ab = MigrationChange.ComputeChainId("sqlserver", "b", "h2", a);
        var b = MigrationChange.ComputeChainId("sqlserver", "b", "h2", null);
        var ba = MigrationChange.ComputeChainId("sqlserver", "a", "h1", b);

        Assert.NotEqual(ab, ba);
        Assert.NotEqual(a, MigrationChange.ComputeChainId("postgresql", "a", "h1", null));
    }
}
