using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using MySqlConnector;

namespace Badger.Trek.Providers.MySql.Scripting;

/// <summary>
/// Shadow databases for 'trek add --from-diff': created on the environment's server with the environment's own
/// connection (it needs the CREATE and DROP rights for databases) and dropped afterwards.
/// </summary>
internal sealed class MySqlScratchDatabases(MySqlProvider provider) : IScratchDatabases
{
    public async Task<ScratchDatabase> CreateAsync(DeploymentEnvironment server, SessionContext context, bool keep, CancellationToken cancellationToken)
    {
        var name = $"trek_shadow_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        await using (var session = await provider.OpenSessionAsync(server, context, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                // The shadow database gets the same character set and collation, so the schemas compare equal.
                await session.ExecuteAsync(
                    $"CREATE DATABASE {MySqlModel.Q(name)} CHARACTER SET @@character_set_database COLLATE @@collation_database;".Replace("@@character_set_database", await Setting(session, "character_set_database", cancellationToken).ConfigureAwait(false), StringComparison.Ordinal).Replace("@@collation_database", await Setting(session, "collation_database", cancellationToken).ConfigureAwait(false), StringComparison.Ordinal),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (MySqlException ex)
            {
                throw new MigrationException($"Could not create the shadow database on {session.Description}: {ex.Message} Pass --shadow-env with a server where you may create databases (a local MySQL container works well).", ex);
            }
        }

        var properties = server.ConnectionProperties.Where(kv => !kv.Key.Equals("Database", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var shadow = server with { Database = name, ConnectionProperties = properties };
        if (shadow.ConnectionString is { } cs)
        {
            shadow = shadow with { ConnectionString = new MySqlConnectionStringBuilder(cs) { Database = name }.ConnectionString };
        }

        return new ScratchDatabase(shadow, async () =>
        {
            if (keep)
            {
                return;
            }

            MySqlConnection.ClearAllPools();
            await using var session = await provider.OpenSessionAsync(server, context, CancellationToken.None).ConfigureAwait(false);
            await session.ExecuteAsync($"DROP DATABASE IF EXISTS {MySqlModel.Q(name)};", null, CancellationToken.None).ConfigureAwait(false);
        });
    }

    private static async Task<string> Setting(IDatabaseSession session, string variable, CancellationToken cancellationToken)
    {
        var value = (string)(await session.QueryAsync($"SELECT @@{variable} AS v;", null, cancellationToken).ConfigureAwait(false))[0]["v"]!;
        return System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_]+$") ? value : throw new MigrationException($"Unexpected {variable} '{value}'.");
    }
}
