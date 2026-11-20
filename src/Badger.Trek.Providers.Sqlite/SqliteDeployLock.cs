using Badger.Trek.Core;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.Sqlite;

/// <summary>
/// SQLite's own locks last one transaction, and a run is many transactions, so trek holds an exclusive lock on a file
/// next to the database (app.db.trek-lock) for the whole run. The file is left in place afterwards: on macOS and
/// Linux, deleting it while another deployer waits for it would let two deployers lock different files.
/// </summary>
public sealed class SqliteDeployLock : IDeployLock
{
    public const string Suffix = ".trek-lock";

    public async Task<IAsyncDisposable> AcquireAsync(IDatabaseSession session, string stateSchema, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var path = session is SqliteSession s ? s.Path + Suffix : throw new InvalidOperationException("Not a SQLite session.");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return stream;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new MigrationException($"Timed out after {timeout.TotalSeconds:0}s waiting for the deploy lock '{path}'. Another trek deploy is running against this database.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new MigrationException($"Could not create the deploy lock file '{path}': {ex.Message}", ex);
            }
        }
    }
}
