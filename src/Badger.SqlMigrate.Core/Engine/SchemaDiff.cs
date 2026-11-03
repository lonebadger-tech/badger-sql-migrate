using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Core.Engine;

public enum SchemaDifferenceKind
{
    /// <summary>Only on the right (target) side.</summary>
    Added,

    /// <summary>Only on the left (source) side.</summary>
    Removed,

    Changed,
}

public sealed record SchemaDifference(SchemaDifferenceKind Kind, string Key, string? Left, string? Right);

/// <summary>Compares two schema dumps entry by entry. Pure; the provider decides what an entry is.</summary>
public static class SchemaDiff
{
    public static IReadOnlyList<SchemaDifference> Compare(IReadOnlyList<SchemaEntry> left, IReadOnlyList<SchemaEntry> right)
    {
        var l = Index(left);
        var r = Index(right);
        var result = new List<SchemaDifference>();
        foreach (var key in l.Keys.Union(r.Keys).Order(StringComparer.Ordinal))
        {
            var hasLeft = l.TryGetValue(key, out var a);
            var hasRight = r.TryGetValue(key, out var b);
            if (hasLeft && hasRight)
            {
                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    result.Add(new SchemaDifference(SchemaDifferenceKind.Changed, key, a, b));
                }
            }
            else
            {
                result.Add(new SchemaDifference(hasLeft ? SchemaDifferenceKind.Removed : SchemaDifferenceKind.Added, key, a, b));
            }
        }

        return result;
    }

    /// <summary>
    /// Line diff of two texts (longest common subsequence), as (' ', '-', '+') lines with at most
    /// <paramref name="context"/> unchanged lines around each change; skipped runs become a "..." line.
    /// </summary>
    public static IReadOnlyList<(char Op, string Line)> Lines(string left, string right, int context = 2)
    {
        var a = left.Split('\n');
        var b = right.Split('\n');
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var all = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                all.Add((' ', a[x++]));
                y++;
            }
            else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                all.Add(('-', a[x++]));
            }
            else
            {
                all.Add(('+', b[y++]));
            }
        }

        var keep = new bool[all.Count];
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Item1 != ' ')
            {
                for (var k = Math.Max(0, i - context); k <= Math.Min(all.Count - 1, i + context); k++)
                {
                    keep[k] = true;
                }
            }
        }

        var result = new List<(char, string)>();
        for (var i = 0; i < all.Count; i++)
        {
            if (keep[i])
            {
                result.Add(all[i]);
            }
            else if (result.Count == 0 || result[^1] != (' ', "..."))
            {
                result.Add((' ', "..."));
            }
        }

        return result;
    }

    /// <summary>Entries whose key repeats within a dump fall back to their full text as key, so none is lost.</summary>
    private static Dictionary<string, string> Index(IReadOnlyList<SchemaEntry> entries)
    {
        var duplicates = entries.GroupBy(e => e.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            index[duplicates.Contains(entry.Key) ? entry.Text : entry.Key] = entry.Text;
        }

        return index;
    }
}
