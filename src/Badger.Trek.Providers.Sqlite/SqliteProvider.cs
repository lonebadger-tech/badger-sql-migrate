using System.Globalization;
using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Badger.Trek.Providers.Sqlite;

/// <summary>SQLite 3 database files (bundled engine: nothing to install).</summary>
public sealed class SqliteProvider(SqliteOptions options) : IDatabaseProvider
{
    public const string ProviderId = "sqlite";

    public SqliteProvider()
        : this(SqliteOptions.Default)
    {
    }

    /// <summary>The project's 'sqlite:' settings (the oldest SQLite that opens its databases).</summary>
    public SqliteOptions Options { get; } = options;

    public string Id => ProviderId;

    public string DisplayName => "SQLite";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        TransactionalDdl = true,
        SupportsDeployLock = true,
        SupportsSchemaFingerprint = true,
    };

    public IScriptParser Parser { get; } = new SqliteStatementSplitter();

    public IScriptLinter Linter { get; } = new SqliteLinter(options);

    public IStateStore StateStore { get; } = new SqliteStateStore();

    public IDeployLock Lock { get; } = new SqliteDeployLock();

    public ISchemaInspector Schema { get; } = new SqliteSchemaInspector();

    public IEnvironmentResources? Resources => null;

    public ISchemaScripter Scripter { get; } = new Scripting.SqliteScripter(options);

    public IScratchDatabases ScratchDatabases { get; } = new Scripting.SqliteScratchDatabases();

    public Core.Conversion.ISchemaExporter Exporter { get; } = new Conversion.SqliteExporter();

    public Core.Conversion.IDataTransfer Data { get; } = new Conversion.SqliteDataTransfer();

    public string DefaultSchema => "main";

    public IReadOnlyList<string> ValidateEnvironment(DeploymentEnvironment environment) => SqliteConnectionFactory.Validate(environment);

    public Endpoint ResolveEndpoint(DeploymentEnvironment environment) => SqliteConnectionFactory.ResolveEndpoint(environment);

    public string? ResolveDatabaseName(DeploymentEnvironment environment) => SqliteConnectionFactory.ResolveDatabaseName(environment);

    public async Task<IDatabaseSession> OpenSessionAsync(DeploymentEnvironment environment, SessionContext context, CancellationToken cancellationToken)
    {
        var builder = SqliteConnectionFactory.Build(environment);
        SqliteConnection connection;
        try
        {
            connection = new SqliteConnection(builder.ConnectionString);
        }
        catch (TypeInitializationException ex) when (ex.InnerException is not null)
        {
            throw new MigrationException("Could not load SQLite: " + (SqliteNativeLibrary.Description ?? ex.GetBaseException().Message), ex);
        }

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or ArgumentException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw SqliteConnectionFactory.CannotOpen(builder.DataSource, ex);
        }

        return new SqliteSession(connection, builder.DataSource);
    }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> DescribeAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        var row = (await session.QueryAsync("""
            SELECT sqlite_version() AS version,
                   (SELECT journal_mode FROM pragma_journal_mode) AS journal_mode,
                   (SELECT encoding FROM pragma_encoding) AS encoding,
                   (SELECT page_size FROM pragma_page_size) AS page_size,
                   (SELECT foreign_keys FROM pragma_foreign_keys) AS foreign_keys
            """, null, cancellationToken).ConfigureAwait(false))[0];
        string S(string key) => Convert.ToString(row[key], CultureInfo.InvariantCulture) ?? "unknown";
        return
        [
            new("server", "SQLite " + S("version")),
            new("engine", SqliteNativeLibrary.Description ?? "built in"),
            new("database", session is SqliteSession s ? s.Path : session.Description),
            new("journal mode", S("journal_mode")),
            new("encoding", S("encoding")),
            new("page size", S("page_size")),
            new("foreign keys", S("foreign_keys") == "1" ? "on" : "off"),
        ];
    }

    /// <summary>SQLite separates statements with semicolons; there is no batch separator line.</summary>
    public string FormatBatchSeparator() => string.Empty;

    public string FormatComment(string text) => "-- " + text;
}
