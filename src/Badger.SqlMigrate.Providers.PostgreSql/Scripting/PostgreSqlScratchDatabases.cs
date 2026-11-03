using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Npgsql;

namespace Badger.SqlMigrate.Providers.PostgreSql.Scripting;

/// <summary>
/// Shadow databases for 'bsm add --from-diff': created on the environment's server through its "postgres"
/// database and dropped afterwards (other sessions on it are ended first).
/// </summary>
internal sealed class PostgreSqlScratchDatabases(PostgreSqlProvider provider) : IScratchDatabases
{
    public async Task<ScratchDatabase> CreateAsync(DeploymentEnvironment server, SessionContext context, bool keep, CancellationToken cancellationToken)
    {
        var name = $"bsm_shadow_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var properties = server.ConnectionProperties.Where(kv => !kv.Key.Equals("Database", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var maintenance = server with { Database = "postgres", ConnectionProperties = properties };
        await using (var session = await provider.OpenSessionAsync(maintenance, context, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await session.ExecuteAsync($"CREATE DATABASE {Sql.Ident(name)};", null, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex)
            {
                throw new MigrationException($"Could not create the shadow database on {session.Description}: {ex.MessageText} Pass --shadow-env with a server where you may create databases (a local PostgreSQL container works well).", ex);
            }
        }

        return new ScratchDatabase(maintenance with { Database = name }, async () =>
        {
            if (keep)
            {
                return;
            }

            NpgsqlConnection.ClearAllPools();
            await using var session = await provider.OpenSessionAsync(maintenance, context, CancellationToken.None).ConfigureAwait(false);
            await session.ExecuteAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid();", new Dictionary<string, object?> { ["name"] = name }, CancellationToken.None).ConfigureAwait(false);
            await session.ExecuteAsync($"DROP DATABASE IF EXISTS {Sql.Ident(name)};", null, CancellationToken.None).ConfigureAwait(false);
        });
    }
}
