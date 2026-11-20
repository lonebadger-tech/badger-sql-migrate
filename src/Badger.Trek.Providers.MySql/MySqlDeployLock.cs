using System.Globalization;
using Badger.Trek.Core;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.MySql;

/// <summary>
/// A named lock (GET_LOCK) held by the session for the whole run. MySQL lock names are server-wide, so the name
/// includes the database: deploys to different databases on one server do not block each other.
/// </summary>
public sealed class MySqlDeployLock : IDeployLock
{
    public async Task<IAsyncDisposable> AcquireAsync(IDatabaseSession session, string stateSchema, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var database = (await session.QueryAsync("SELECT DATABASE() AS db;", null, cancellationToken).ConfigureAwait(false))[0]["db"] as string ?? string.Empty;

        // Lock names are at most 64 characters.
        var key = $"trek:{database}:{stateSchema}";
        key = key.Length <= 64 ? key : key[..64];
        var p = new Dictionary<string, object?> { ["key"] = key };
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var rows = await session.QueryAsync("SELECT GET_LOCK(@key, 0) AS acquired;", p, cancellationToken).ConfigureAwait(false);
            if (rows[0]["acquired"] is { } acquired && Convert.ToInt64(acquired, CultureInfo.InvariantCulture) == 1)
            {
                return new Releaser(session, p);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new MigrationException($"Timed out after {timeout.TotalSeconds:0}s waiting for the deploy lock '{key}'. Another trek deploy is running against this database.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Releaser(IDatabaseSession session, Dictionary<string, object?> parameters) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await session.ExecuteAsync("DO RELEASE_LOCK(@key);", parameters, CancellationToken.None).ConfigureAwait(false);
    }
}
