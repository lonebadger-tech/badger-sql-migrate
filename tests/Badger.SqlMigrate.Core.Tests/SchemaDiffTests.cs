using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Core.Tests;

public class SchemaDiffTests
{
    private static SchemaEntry E(string key, string text) => new(key, text);

    [Fact]
    public void Reports_added_removed_and_changed_entries_by_key()
    {
        var left = new[] { E("t1", "t1|a"), E("t2", "t2|a"), E("v", "view\nselect 1") };
        var right = new[] { E("t1", "t1|a"), E("t3", "t3|a"), E("v", "view\nselect 2") };

        var diff = SchemaDiff.Compare(left, right);

        Assert.Equal(
            [(SchemaDifferenceKind.Removed, "t2"), (SchemaDifferenceKind.Added, "t3"), (SchemaDifferenceKind.Changed, "v")],
            diff.Select(d => (d.Kind, d.Key)));
    }

    [Fact]
    public void Duplicate_keys_fall_back_to_full_text()
    {
        var left = new[] { E("check t <system>", "check|a"), E("check t <system>", "check|b") };
        var right = new[] { E("check t <system>", "check|a"), E("check t <system>", "check|c") };

        var diff = SchemaDiff.Compare(left, right);

        Assert.Equal([(SchemaDifferenceKind.Removed, "check|b"), (SchemaDifferenceKind.Added, "check|c")], diff.Select(d => (d.Kind, d.Key)));
    }

    [Fact]
    public void Line_diff_shows_changes_with_context()
    {
        var left = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}"));
        var right = left.Replace("line 5", "line five", StringComparison.Ordinal);

        var lines = SchemaDiff.Lines(left, right, context: 1);

        Assert.Equal(
            [(' ', "..."), (' ', "line 4"), ('-', "line 5"), ('+', "line five"), (' ', "line 6"), (' ', "...")],
            lines);
    }
}
