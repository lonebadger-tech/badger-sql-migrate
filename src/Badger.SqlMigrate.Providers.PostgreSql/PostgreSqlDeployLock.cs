using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.PostgreSql;

/// <summary>A session-level advisory lock (scoped to the database) held for the whole run.</summary>
public sealed class PostgreSqlDeployLock : IDeployLock
{
    public async Task<IAsyncDisposable> AcquireAsync(IDatabaseSession session, string stateSchema, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var key = $"bsm:{stateSchema}";
        var p = new Dictionary<string, object?> { ["key"] = key };
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var rows = await session.QueryAsync("SELECT pg_try_advisory_lock(hashtextextended(@key, 0)) AS acquired;", p, cancellationToken).ConfigureAwait(false);
            if (rows[0]["acquired"] is true)
            {
                return new Releaser(session, p);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new MigrationException($"Timed out after {timeout.TotalSeconds:0}s waiting for the deploy lock '{key}'. Another bsm deploy is running against this database.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Releaser(IDatabaseSession session, Dictionary<string, object?> parameters) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await session.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@key, 0));", parameters, CancellationToken.None).ConfigureAwait(false);
    }
}
