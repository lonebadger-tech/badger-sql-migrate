using System.Globalization;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer.Scripting;

/// <summary>
/// Creates a temporary database next to the environment's (CREATE DATABASE from master) and drops it afterwards.
/// On Azure SQL Database this needs rights in master, takes a minute and is billed while it exists; point
/// --shadow-env at a local server instead.
/// </summary>
internal sealed class SqlServerScratchDatabases(SqlServerProvider provider) : IScratchDatabases
{
    public async Task<ScratchDatabase> CreateAsync(DeploymentEnvironment server, SessionContext context, bool keep, CancellationToken cancellationToken)
    {
        var name = $"bsm_shadow_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var properties = server.ConnectionProperties.Where(kv => !kv.Key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase) && !kv.Key.Equals("Database", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var master = server with { Database = "master", ConnectionProperties = properties };
        await using (var session = await provider.OpenSessionAsync(master, context, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await session.ExecuteAsync($"CREATE DATABASE {Sql.Quote(name)};", null, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                throw new MigrationException($"Could not create the shadow database on {session.Description}: {ex.Message} Pass --shadow-env with a server where you may create databases (a local SQL Server container works well).", ex);
            }

            // Azure SQL Database creates databases asynchronously.
            for (var i = 0; i < 300; i++)
            {
                var rows = await session.QueryAsync("SELECT state_desc FROM sys.databases WHERE name = @name;", new Dictionary<string, object?> { ["name"] = name }, cancellationToken).ConfigureAwait(false);
                if (rows.Count > 0 && string.Equals(Convert.ToString(rows[0]["state_desc"], CultureInfo.InvariantCulture), "ONLINE", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }

        return new ScratchDatabase(master with { Database = name }, async () =>
        {
            if (keep)
            {
                return;
            }

            SqlConnection.ClearAllPools();
            await using var session = await provider.OpenSessionAsync(master, context, CancellationToken.None).ConfigureAwait(false);
            var engine = (await session.QueryAsync("SELECT CONVERT(int, SERVERPROPERTY('EngineEdition')) AS e;", null, CancellationToken.None).ConfigureAwait(false))[0]["e"];
            var single = Convert.ToInt32(engine, CultureInfo.InvariantCulture) == 5 ? string.Empty : $"ALTER DATABASE {Sql.Quote(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ";
            await session.ExecuteAsync($"{single}DROP DATABASE {Sql.Quote(name)};", null, CancellationToken.None).ConfigureAwait(false);
        });
    }
}
