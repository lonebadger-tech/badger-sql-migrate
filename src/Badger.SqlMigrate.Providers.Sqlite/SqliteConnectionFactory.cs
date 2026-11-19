using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Badger.SqlMigrate.Providers.Sqlite;

/// <summary>
/// Builds Microsoft.Data.Sqlite connections from an environment: 'database' is the file (relative paths resolve from
/// the working directory, like the sqlite3 shell; ~ is the home folder), or 'connectionString' (Data Source=...).
/// Any connection-string keyword under connectionProperties is applied last. A missing file is created.
/// </summary>
public static class SqliteConnectionFactory
{
    private static readonly string[] NetworkSettings = ["host", "port", "user", "password", "ssh", "authentication", "tls"];

    public static IReadOnlyList<string> Validate(DeploymentEnvironment environment)
    {
        var problems = new List<string>();
        var name = environment.Name;
        if (string.IsNullOrWhiteSpace(environment.Database) && environment.ConnectionString is null && !environment.ConnectionProperties.Keys.Any(IsDataSourceKey))
        {
            problems.Add($"environment '{name}': set 'database' to the SQLite file (e.g. database: data/app.db) or 'connectionString' (Data Source=...).");
        }

        if (environment.ConnectionString is not null)
        {
            try
            {
                _ = new SqliteConnectionStringBuilder(environment.ConnectionString);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"environment '{name}': invalid connectionString: {ex.Message}");
            }
        }

        foreach (var (key, value) in environment.ConnectionProperties)
        {
            try
            {
                _ = new SqliteConnectionStringBuilder { [key] = value }.ConnectionString;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                problems.Add($"environment '{name}': connectionProperties '{key}': {ex.Message}");
            }
        }

        var present = new List<string>();
        if (!string.IsNullOrWhiteSpace(environment.Host))
        {
            present.Add("host");
        }

        if (environment.Port is not null)
        {
            present.Add("port");
        }

        if (!string.IsNullOrEmpty(environment.User))
        {
            present.Add("user");
        }

        if (!string.IsNullOrEmpty(environment.Password))
        {
            present.Add("password");
        }

        if (environment.Ssh is not null)
        {
            present.Add("ssh");
        }

        if (environment.Authentication is not null)
        {
            present.Add("authentication");
        }

        if (environment.Tls.Mode != TlsMode.Default || environment.Tls.ServerCertificate is not null || environment.Tls.HostNameInCertificate is not null || environment.Tls.TrustServerCertificate)
        {
            present.Add("tls");
        }

        foreach (var setting in present.Where(NetworkSettings.Contains))
        {
            problems.Add($"environment '{name}': '{setting}' does not apply to SQLite: the database is a local file named by 'database'.");
        }

        if (problems.Count == 0)
        {
            try
            {
                var builder = Build(environment);
                if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource is "" or ":memory:" || builder.DataSource.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"environment '{name}': an in-memory SQLite database is gone when the connection closes, so bsm's journal would not survive the run. Use a file.");
                }
            }
            catch (ArgumentException ex)
            {
                problems.Add($"environment '{name}': {ex.Message}");
            }
        }

        foreach (var key in environment.ProviderSettings.Select(kv => kv.Key))
        {
            problems.Add($"environment '{name}': unknown SQLite setting '{key}'. SQLite has no provider-specific settings; use connectionProperties for driver keywords.");
        }

        return problems;
    }

    /// <summary>SQLite has no server; there is nothing to tunnel to.</summary>
    public static Endpoint ResolveEndpoint(DeploymentEnvironment environment) =>
        throw new ArgumentException("SQLite databases are local files; they have no host or port.");

    public static string? ResolveDatabaseName(DeploymentEnvironment environment)
    {
        try
        {
            var path = Build(environment).DataSource;
            return path.Length == 0 ? null : Path.GetFileNameWithoutExtension(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The full path of the database file.</summary>
    public static string ResolvePath(DeploymentEnvironment environment) => Build(environment).DataSource;

    public static SqliteConnectionStringBuilder Build(DeploymentEnvironment environment)
    {
        var b = environment.ConnectionString is null ? new SqliteConnectionStringBuilder() : new SqliteConnectionStringBuilder(environment.ConnectionString);

        // Files must not stay open after a session ends: the shadow database is deleted, and other tools want the file.
        b.Pooling = false;
        foreach (var (key, value) in environment.ConnectionProperties)
        {
            b[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(environment.Database))
        {
            b.DataSource = environment.Database;
        }

        b.DataSource = FullPath(b.DataSource);
        return b;
    }

    private static string FullPath(string dataSource)
    {
        if (dataSource.Length == 0 || dataSource == ":memory:" || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return dataSource;
        }

        if (dataSource == "~" || dataSource.StartsWith("~/", StringComparison.Ordinal) || dataSource.StartsWith("~\\", StringComparison.Ordinal))
        {
            dataSource = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dataSource.Length > 2 ? dataSource[2..] : string.Empty);
        }

        return Path.GetFullPath(dataSource);
    }

    private static bool IsDataSourceKey(string key) =>
        key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) || key.Equals("DataSource", StringComparison.OrdinalIgnoreCase) || key.Equals("Filename", StringComparison.OrdinalIgnoreCase);

    internal static MigrationException CannotOpen(string path, Exception ex) =>
        new($"Could not open the SQLite database {path}: {ex.Message}", ex);
}
