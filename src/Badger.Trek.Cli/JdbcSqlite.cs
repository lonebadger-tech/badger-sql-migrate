using System.Text.Json.Nodes;
using Badger.Trek.Core.Import;

namespace Badger.Trek.Cli;

/// <summary>Maps a SQLite JDBC URL (jdbc:sqlite:path/to/app.db, optionally ?key=value) to the environment's 'database'.</summary>
public static class JdbcSqlite
{
    private const string Prefix = "jdbc:sqlite:";

    public static JsonObject? Map(ConnectionToImport connection, List<string> warnings)
    {
        var where = $"environment '{connection.EnvironmentName}'";
        var url = connection.Url.Trim();
        if (!url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"{where}: '{url}' is not a SQLite JDBC URL; set 'database' in trek.yaml by hand.");
            return null;
        }

        var path = url[Prefix.Length..];
        var query = path.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            warnings.Add($"{where}: the URL's options ({path[(query + 1)..]}) were not copied; add the ones you need under connectionProperties.");
            path = path[..query];
        }

        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            path = path[5..];
        }

        if (path.Length == 0 || path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"{where}: '{url}' is an in-memory database; set 'database' to a file in trek.yaml.");
            return null;
        }

        return new JsonObject { ["database"] = Uri.UnescapeDataString(path) };
    }
}
