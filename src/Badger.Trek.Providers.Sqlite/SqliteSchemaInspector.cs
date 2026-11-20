using Badger.Trek.Core.Providers;
using Badger.Trek.Providers.Sqlite.Scripting;

namespace Badger.Trek.Providers.Sqlite;

/// <summary>
/// Canonical, sorted dump of the user schema for the fingerprint: tables column by column and constraint by
/// constraint, indexes, views and triggers, each normalized (layout and identifier quoting ignored). SQLite's own
/// tables (sqlite_*), FTS shadow tables and the trek state tables are left out.
/// </summary>
public sealed class SqliteSchemaInspector : ISchemaInspector
{
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "table", "virtual table", "column", "constraint", "index", "view", "trigger" };

    public async Task<string> CaptureAsync(IDatabaseSession session, string stateSchema, CancellationToken cancellationToken) =>
        (await SqliteModel.ReadAsync(session, stateSchema, _ => false, cancellationToken).ConfigureAwait(false)).Dump();

    /// <summary>Entries keyed by kind and object, e.g. "column main.customer.email", "index main.customer.ix_email".</summary>
    public IReadOnlyList<SchemaEntry> Parse(string dump)
    {
        var entries = new List<SchemaEntry>();
        var current = new List<string>();
        foreach (var line in dump.Split('\n'))
        {
            var bar = line.IndexOf('|', StringComparison.Ordinal);
            if (bar > 0 && Kinds.Contains(line[..bar]) && current.Count > 0)
            {
                entries.Add(Entry(current));
                current.Clear();
            }

            current.Add(line);
        }

        if (current.Count > 0 && !(current.Count == 1 && current[0].Length == 0))
        {
            entries.Add(Entry(current));
        }

        return entries;

        static SchemaEntry Entry(List<string> lines)
        {
            var text = string.Join('\n', lines);
            var f = lines[0].Split('|');
            string Field(int i) => i < f.Length ? f[i] : string.Empty;
            var key = f[0] switch
            {
                "column" => $"column {Field(1)}.{Field(3)}",
                "index" or "trigger" => $"{f[0]} {Field(1)}.{Field(2)}",
                "constraint" => $"constraint {Field(1)} {string.Join('|', f.Skip(2))}",
                _ => $"{f[0]} {Field(1)}",
            };
            return new SchemaEntry(key, text);
        }
    }
}
