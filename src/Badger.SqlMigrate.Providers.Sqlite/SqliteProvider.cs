using System.Globalization;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Badger.SqlMigrate.Providers.Sqlite;

/// <summary>SQLite 3 database files (bundled engine: nothing to install).</summary>
public sealed class SqliteProvider : IDatabaseProvider
{
    public const string ProviderId = "sqlite";

    public string Id => ProviderId;

    public string DisplayName => "SQLite";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        TransactionalDdl = true,
        SupportsDeployLock = true,
        SupportsSchemaFingerprint = true,
    };

    public IScriptParser Parser { get; } = new SqliteStatementSplitter();

    public IScriptLinter Linter { get; } = new SqliteLinter();

    public IStateStore StateStore { get; } = new SqliteStateStore();

    public IDeployLock Lock { get; } = new SqliteDeployLock();

    public ISchemaInspector Schema { get; } = new SqliteSchemaInspector();

    public IEnvironmentResources? Resources => null;

    public ISchemaScripter Scripter { get; } = new Scripting.SqliteScripter();

    public IScratchDatabases ScratchDatabases { get; } = new Scripting.SqliteScratchDatabases();

    public string DefaultSchema => "main";

    public IReadOnlyList<string> ValidateEnvironment(DeploymentEnvironment environment) => SqliteConnectionFactory.Validate(environment);

    public Endpoint ResolveEndpoint(DeploymentEnvironment environment) => SqliteConnectionFactory.ResolveEndpoint(environment);

    public string? ResolveDatabaseName(DeploymentEnvironment environment) => SqliteConnectionFactory.ResolveDatabaseName(environment);

    public async Task<IDatabaseSession> OpenSessionAsync(DeploymentEnvironment environment, SessionContext context, CancellationToken cancellationToken)
    {
        var builder = SqliteConnectionFactory.Build(environment);
        var connection = new SqliteConnection(builder.ConnectionString);
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
