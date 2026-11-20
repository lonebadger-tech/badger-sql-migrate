using System.Globalization;
using System.Text.RegularExpressions;
using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using MySqlConnector;

namespace Badger.Trek.Providers.MySql;

/// <summary>
/// MySQL 8.4 LTS and 9.7 LTS (including Azure Database for MySQL, Amazon RDS/Aurora MySQL and Google Cloud SQL).
/// MySQL commits DDL implicitly, so changes run without a transaction: a change that fails part-way can leave what it
/// already did in place, and the journal records only finished changes.
/// </summary>
public sealed partial class MySqlProvider : IDatabaseProvider
{
    public const string ProviderId = "mysql";

    public string Id => ProviderId;

    public string DisplayName => "MySQL";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        TransactionalDdl = false,
        SupportsDeployLock = true,
        SupportsSchemaFingerprint = true,
        SupportedAuthentication = MySqlConnectionFactory.Supported,
    };

    public IScriptParser Parser { get; } = new MySqlStatementSplitter();

    public IScriptLinter Linter { get; } = new MySqlLinter();

    public IStateStore StateStore { get; } = new MySqlStateStore();

    public IDeployLock Lock { get; } = new MySqlDeployLock();

    public ISchemaInspector Schema { get; } = new MySqlSchemaInspector();

    public IEnvironmentResources? Resources => null;

    public ISchemaScripter Scripter { get; } = new Scripting.MySqlScripter();

    public IScratchDatabases ScratchDatabases => new Scripting.MySqlScratchDatabases(this);

    /// <summary>In MySQL a schema is a database; trek works inside the environment's database.</summary>
    public string DefaultSchema => string.Empty;

    public string QuoteIdentifier(string name) => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";

    public IReadOnlyList<string> ValidateEnvironment(DeploymentEnvironment environment) => MySqlConnectionFactory.Validate(environment);

    public Endpoint ResolveEndpoint(DeploymentEnvironment environment) => MySqlConnectionFactory.ResolveEndpoint(environment);

    public string? ResolveDatabaseName(DeploymentEnvironment environment) => MySqlConnectionFactory.ResolveDatabaseName(environment);

    public async Task<IDatabaseSession> OpenSessionAsync(DeploymentEnvironment environment, SessionContext context, CancellationToken cancellationToken)
    {
        var (builder, description) = await MySqlConnectionFactory.BuildAsync(environment, context, cancellationToken).ConfigureAwait(false);
        var connection = new MySqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or ArgumentException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new MigrationException($"Could not connect to {description}: {ex.Message}", ex);
        }

        return new MySqlSession(connection, description);
    }

    /// <summary>
    /// A note for 'trek doctor' when the server is not a MySQL release trek supports: the LTS releases 8.4 and 9.7.
    /// Older releases are past their support; Innovation releases (8.1-8.3, 9.0-9.6) are short-lived; MariaDB is
    /// a different database.
    /// </summary>
    public static string UnsupportedVersionNote(string version)
    {
        if (version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
        {
            return " - not supported: MariaDB is not MySQL; Badger Trek supports MySQL 8.4 and 9.7 (LTS)";
        }

        var m = VersionRegex().Match(version);
        if (!m.Success)
        {
            return string.Empty;
        }

        var (major, minor) = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        return (major, minor) switch
        {
            (8, 4) or (9, 7) => string.Empty,
            _ when major < 8 || (major == 8 && minor < 4) => $" - not supported: MySQL {major}.{minor} is past its end of life; Badger Trek supports MySQL 8.4 and 9.7 (LTS)",
            _ when major == 9 && minor < 7 => $" - not supported: MySQL {major}.{minor} is an Innovation release; Badger Trek supports the LTS releases 8.4 and 9.7",
            _ => " - not tested: Badger Trek is tested on MySQL 8.4 and 9.7 (LTS)",
        };
    }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> DescribeAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        var row = (await session.QueryAsync("""
            SELECT VERSION() AS version, @@version_comment AS comment, CURRENT_USER() AS login, DATABASE() AS db,
                   @@character_set_database AS charset, @@collation_database AS coll,
                   (SELECT variable_value FROM performance_schema.session_status WHERE variable_name = 'Ssl_version') AS tls
            """, null, cancellationToken).ConfigureAwait(false))[0];
        string S(string key) => Convert.ToString(row[key], CultureInfo.InvariantCulture) ?? string.Empty;
        var version = S("version");
        return
        [
            new("server", $"MySQL {version} ({S("comment")}){UnsupportedVersionNote(version + " " + S("comment"))}"),
            new("login", S("login")),
            new("database", S("db")),
            new("encrypted", S("tls") is { Length: > 0 } tls ? $"yes ({tls})" : "no"),
            new("charset", $"{S("charset")} ({S("coll")})"),
        ];
    }

    /// <summary>Statements end at the delimiter; there is no batch separator line.</summary>
    public string FormatBatchSeparator() => string.Empty;

    public string FormatComment(string text) => "-- " + text;

    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex VersionRegex();
}
