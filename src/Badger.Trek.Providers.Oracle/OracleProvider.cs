using System.Globalization;
using System.Text.RegularExpressions;
using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using Oracle.ManagedDataAccess.Client;

namespace Badger.Trek.Providers.Oracle;

/// <summary>
/// Oracle Database 19c and 26ai, the long-term releases Oracle still supports (including Autonomous Database and
/// Oracle Database Free). trek deploys to one schema per environment (the user's own, or 'schema'), and keeps its journal
/// there as TREK_* tables. Oracle commits DDL implicitly, so changes run without a transaction: a change that fails
/// part-way keeps what it already did, and the journal records only finished changes.
/// </summary>
public sealed partial class OracleProvider(OracleOptions options) : IDatabaseProvider
{
    public const string ProviderId = "oracle";

    public OracleProvider()
        : this(OracleOptions.Default)
    {
    }

    public OracleOptions Options => options;

    public string Id => ProviderId;

    public string DisplayName => "Oracle";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        TransactionalDdl = false,
        SupportsDeployLock = true,
        SupportsSchemaFingerprint = true,
        SupportedAuthentication = OracleConnectionFactory.Supported,
    };

    public IScriptParser Parser { get; } = new OracleStatementSplitter();

    public IScriptLinter Linter { get; } = new OracleLinter(options);

    public IStateStore StateStore { get; } = new OracleStateStore();

    public IDeployLock Lock { get; } = new OracleDeployLock();

    public ISchemaInspector Schema { get; } = new OracleSchemaInspector();

    public IEnvironmentResources? Resources => null;

    /// <summary>A schema is a user; trek works inside the environment's schema.</summary>
    public string DefaultSchema => string.Empty;

    public IReadOnlyList<string> ValidateEnvironment(DeploymentEnvironment environment) => OracleConnectionFactory.Validate(environment);

    public Endpoint ResolveEndpoint(DeploymentEnvironment environment) => OracleConnectionFactory.ResolveEndpoint(environment);

    public string? ResolveDatabaseName(DeploymentEnvironment environment) => OracleConnectionFactory.ResolveDatabaseName(environment);

    public async Task<IDatabaseSession> OpenSessionAsync(DeploymentEnvironment environment, SessionContext context, CancellationToken cancellationToken)
    {
        var (builder, description) = OracleConnectionFactory.Build(environment, context);
        var connection = new OracleConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OracleException or InvalidOperationException or ArgumentException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new MigrationException($"Could not connect to {description}: {ex.Message}", ex);
        }

        try
        {
            string user;
            await using (var who = new OracleCommand("SELECT SYS_CONTEXT('USERENV', 'SESSION_USER') FROM dual", connection))
            {
                user = (string)(await who.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            var schema = OracleConnectionFactory.ConfiguredSchema(environment) ?? user;
            if (schema != user)
            {
                // Unqualified names in scripts go to the configured schema, as they would for its owner.
                await using var set = new OracleCommand($"ALTER SESSION SET CURRENT_SCHEMA = {OracleStateStore.Quote(schema)}", connection);
                await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new OracleSession(connection, $"{description} as {schema}", schema, builder.ConnectionString);
        }
        catch (OracleException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new MigrationException(ex.Number == 1435
                ? $"Schema '{OracleConnectionFactory.ConfiguredSchema(environment)}' (environment '{environment.Name}' setting 'schema') does not exist on {description}."
                : $"Could not prepare the session on {description}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A note for 'trek doctor' when the server is not a release trek supports: 19c and 26ai (version 23.x), Oracle's
    /// long-term releases. 21c is an innovation release; older releases are past Premier Support.
    /// </summary>
    public static string UnsupportedVersionNote(string version)
    {
        var m = VersionRegex().Match(version);
        if (!m.Success)
        {
            return string.Empty;
        }

        var major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return major switch
        {
            19 or 23 => string.Empty,
            < 19 => $" - not supported: Oracle {major} is past Premier Support; Badger Trek supports 19c and 26ai",
            21 => " - not supported: Oracle 21c is an innovation release; Badger Trek supports the long-term releases 19c and 26ai",
            _ => " - not tested: Badger Trek is tested on Oracle 26ai and supports 19c",
        };
    }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> DescribeAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        var product = (await session.QueryAsync("SELECT product, version_full FROM product_component_version WHERE product LIKE 'Oracle%' ORDER BY product FETCH FIRST 1 ROWS ONLY", null, cancellationToken).ConfigureAwait(false))[0];
        var row = (await session.QueryAsync("""
            SELECT SYS_CONTEXT('USERENV', 'SESSION_USER') AS login, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AS schema_name,
                   SYS_CONTEXT('USERENV', 'SERVICE_NAME') AS service, SYS_CONTEXT('USERENV', 'NETWORK_PROTOCOL') AS protocol,
                   (SELECT value FROM nls_database_parameters WHERE parameter = 'NLS_CHARACTERSET') AS charset
              FROM dual
            """, null, cancellationToken).ConfigureAwait(false))[0];
        string S(IReadOnlyDictionary<string, object?> r, string key) => Convert.ToString(r[key], CultureInfo.InvariantCulture) ?? string.Empty;
        var version = S(product, "version_full");
        return
        [
            new("server", $"{S(product, "product").Trim()} {version}{UnsupportedVersionNote(version)}"),
            new("login", S(row, "login")),
            new("schema", S(row, "schema_name")),
            new("service", S(row, "service")),
            new("encrypted", S(row, "protocol").Equals("tcps", StringComparison.OrdinalIgnoreCase) ? "yes (TCPS)" : "no"),
            new("charset", S(row, "charset")),
        ];
    }

    /// <summary>A line holding only '/' runs the statement or PL/SQL block before it, as in SQL*Plus.</summary>
    public string FormatBatchSeparator() => "/";

    public string FormatComment(string text) => "-- " + text;

    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex VersionRegex();
}
