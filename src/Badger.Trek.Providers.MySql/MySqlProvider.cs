using System.Globalization;
using System.Text.RegularExpressions;
using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using MySqlConnector;

namespace Badger.Trek.Providers.MySql;

/// <summary>Which database a <see cref="MySqlProvider"/> talks to.</summary>
public enum MySqlFlavor
{
    /// <summary>MySQL 8.4 LTS and 9.7 LTS.</summary>
    MySql,

    /// <summary>MariaDB 10.11, 11.4, 11.8 and 12.3 LTS.</summary>
    MariaDb,
}

/// <summary>
/// MySQL 8.4 LTS and 9.7 LTS (including Azure Database for MySQL, Amazon RDS/Aurora MySQL and Google Cloud SQL), and,
/// as provider 'mariadb', MariaDB 10.11, 11.4, 11.8 and 12.3 LTS (including Amazon RDS for MariaDB).
/// Both commit DDL implicitly, so changes run without a transaction: a change that fails part-way can leave what it
/// already did in place, and the journal records only finished changes.
/// </summary>
public sealed partial class MySqlProvider(MySqlFlavor flavor = MySqlFlavor.MySql) : IDatabaseProvider
{
    public const string ProviderId = "mysql";

    public const string MariaDbProviderId = "mariadb";

    /// <summary>True for the providers of this assembly: 'mysql' and 'mariadb'.</summary>
    public static bool IsMySqlFamily(string providerId) => providerId is ProviderId or MariaDbProviderId;

    public MySqlFlavor Flavor => flavor;

    public string Id => flavor == MySqlFlavor.MariaDb ? MariaDbProviderId : ProviderId;

    public string DisplayName => flavor == MySqlFlavor.MariaDb ? "MariaDB" : "MySQL";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        TransactionalDdl = false,
        SupportsDeployLock = true,
        SupportsSchemaFingerprint = true,
        SupportedAuthentication = MySqlConnectionFactory.SupportedFor(flavor),
    };

    public IScriptParser Parser { get; } = new MySqlStatementSplitter();

    public IScriptLinter Linter { get; } = new MySqlLinter();

    public IStateStore StateStore { get; } = new MySqlStateStore();

    public IDeployLock Lock { get; } = new MySqlDeployLock();

    public ISchemaInspector Schema { get; } = new MySqlSchemaInspector();

    public IEnvironmentResources? Resources => null;

    public ISchemaScripter Scripter { get; } = new Scripting.MySqlScripter();

    public IScratchDatabases ScratchDatabases => new Scripting.MySqlScratchDatabases(this);

    public Core.Conversion.ISchemaExporter Exporter { get; } = new Conversion.MySqlExporter();

    public Core.Conversion.IDataTransfer Data { get; } = new Conversion.MySqlDataTransfer();

    /// <summary>In MySQL a schema is a database; trek works inside the environment's database.</summary>
    public string DefaultSchema => string.Empty;

    public string QuoteIdentifier(string name) => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";

    public IReadOnlyList<string> ValidateEnvironment(DeploymentEnvironment environment) => MySqlConnectionFactory.Validate(environment, flavor);

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

        // MySQL and MariaDB have drifted apart (catalog columns, types, functions): each provider works with its own.
        var mariaDb = connection.ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        if (mariaDb != (flavor == MySqlFlavor.MariaDb))
        {
            var server = connection.ServerVersion;
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new MigrationException(mariaDb
                ? $"{description} is MariaDB ({server}), not MySQL: use provider 'mariadb' ('provider: mariadb' in trek.yaml, or --from-provider mariadb)."
                : $"{description} is MySQL ({server}), not MariaDB: use provider 'mysql' ('provider: mysql' in trek.yaml, or --from-provider mysql).");
        }

        return new MySqlSession(connection, description, mariaDb);
    }

    /// <summary>
    /// A note for 'trek doctor' when the server is not a release trek supports. MySQL: the LTS releases 8.4 and 9.7;
    /// older releases are past their support and Innovation releases (8.1-8.3, 9.0-9.6) are short-lived. MariaDB: the
    /// LTS releases still supported, 10.11, 11.4, 11.8 and 12.3; others are short-term or past their end of life.
    /// </summary>
    public static string UnsupportedVersionNote(string version)
    {
        var m = VersionRegex().Match(version);
        if (!m.Success)
        {
            return string.Empty;
        }

        var (major, minor) = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        if (version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
        {
            return (major, minor) switch
            {
                (10, 11) or (11, 4) or (11, 8) or (12, 3) => string.Empty,
                _ when major < 10 || (major == 10 && minor < 11) => $" - not supported: MariaDB {major}.{minor} is past its end of life; Badger Trek supports MariaDB 10.11, 11.4, 11.8 and 12.3 (LTS)",
                _ when major < 12 || (major == 12 && minor < 3) => $" - not supported: MariaDB {major}.{minor} is a short-term release; Badger Trek supports the LTS releases 10.11, 11.4, 11.8 and 12.3",
                _ => " - not tested: Badger Trek is tested on MariaDB 10.11, 11.4, 11.8 and 12.3 (LTS)",
            };
        }

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
                   @@character_set_database AS charset, @@collation_database AS coll
            """, null, cancellationToken).ConfigureAwait(false))[0];
        string S(string key) => Convert.ToString(row[key], CultureInfo.InvariantCulture) ?? string.Empty;

        // SHOW STATUS works with performance_schema off (MariaDB's default).
        var tls = (await session.QueryAsync("SHOW SESSION STATUS LIKE 'Ssl_version';", null, cancellationToken).ConfigureAwait(false))
            .Select(r => Convert.ToString(r["Value"], CultureInfo.InvariantCulture)).FirstOrDefault() ?? string.Empty;
        var version = S("version");
        return
        [
            new("server", $"{DisplayName} {version} ({S("comment")}){UnsupportedVersionNote(version + " " + S("comment"))}"),
            new("login", S("login")),
            new("database", S("db")),
            new("encrypted", tls.Length > 0 ? $"yes ({tls})" : "no"),
            new("charset", $"{S("charset")} ({S("coll")})"),
        ];
    }

    /// <summary>Statements end at the delimiter; there is no batch separator line.</summary>
    public string FormatBatchSeparator() => string.Empty;

    public string FormatComment(string text) => "-- " + text;

    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex VersionRegex();
}
