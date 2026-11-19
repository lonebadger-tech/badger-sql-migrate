using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.Sqlite;

/// <summary>
/// SQLite's own locks last one transaction, and a run is many transactions, so bsm holds an exclusive lock on a file
/// next to the database (app.db.bsm-lock) for the whole run. The file is left in place afterwards: on macOS and
/// Linux, deleting it while another deployer waits for it would let two deployers lock different files.
/// </summary>
public sealed class SqliteDeployLock : IDeployLock
{
    public const string Suffix = ".bsm-lock";

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
                throw new MigrationException($"Timed out after {timeout.TotalSeconds:0}s waiting for the deploy lock '{path}'. Another bsm deploy is running against this database.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new MigrationException($"Could not create the deploy lock file '{path}': {ex.Message}", ex);
            }
        }
    }
}
