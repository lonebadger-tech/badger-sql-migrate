using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.Sqlite.Scripting;

/// <summary>Shadow databases for 'bsm add --from-diff': a new file in the temp folder, deleted afterwards.</summary>
internal sealed class SqliteScratchDatabases : IScratchDatabases
{
    public Task<ScratchDatabase> CreateAsync(DeploymentEnvironment server, SessionContext context, bool keep, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bsm_shadow_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}.db");
        var properties = server.ConnectionProperties
            .Where(kv => !kv.Key.Replace(" ", string.Empty, StringComparison.Ordinal).Equals("DataSource", StringComparison.OrdinalIgnoreCase)
                         && !kv.Key.Equals("Filename", StringComparison.OrdinalIgnoreCase)
                         && !kv.Key.Equals("Mode", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var shadow = server with { Database = path, ConnectionString = null, ConnectionProperties = properties };
        return Task.FromResult(new ScratchDatabase(shadow, () =>
        {
            if (!keep)
            {
                foreach (var file in new[] { path, path + "-journal", path + "-wal", path + "-shm", path + SqliteDeployLock.Suffix })
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        // Still open somewhere: the temp folder is cleaned eventually.
                    }
                }
            }

            return ValueTask.CompletedTask;
        }));
    }
}
