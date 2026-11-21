using Badger.Trek.Core;
using Badger.Trek.Core.Providers;
using Oracle.ManagedDataAccess.Client;

namespace Badger.Trek.Providers.Oracle;

/// <summary>
/// A row lock on TREK_LOCK in the deploy schema, held by a second connection for the whole run. Oracle commits DDL
/// implicitly, which would release a lock held by the deploying connection itself; DBMS_LOCK would survive that but
/// needs an EXECUTE grant few deploy logins have. A second connection needs nothing beyond CREATE TABLE, which a deploy
/// login has anyway.
/// </summary>
public sealed class OracleDeployLock : IDeployLock
{
    public async Task<IAsyncDisposable> AcquireAsync(IDatabaseSession session, string stateSchema, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var oracle = session as OracleSession ?? throw new ArgumentException("Not an Oracle session.", nameof(session));
        var table = $"{OracleStateStore.Quote(oracle.Schema)}.{OracleStateStore.Prefix(stateSchema)}{OracleStateStore.LockTable.ToUpperInvariant()}";
        if (!await OracleStateStore.TableExistsAsync(session, OracleStateStore.Prefix(stateSchema) + OracleStateStore.LockTable.ToUpperInvariant(), cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await session.ExecuteAsync($"CREATE TABLE {table} (id NUMBER(1) NOT NULL PRIMARY KEY)", null, cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException ex) when (ex.Number == 955)
            {
                // Another deployer created it first.
            }
        }

        try
        {
            await session.ExecuteAsync($"INSERT INTO {table} (id) SELECT 1 FROM dual WHERE NOT EXISTS (SELECT 1 FROM {table})", null, cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException ex) when (ex.Number == 1)
        {
            // Another deployer inserted it first.
        }

        var connection = new OracleConnection(oracle.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var transaction = connection.BeginTransaction();
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                try
                {
                    await using var command = new OracleCommand($"SELECT id FROM {table} WHERE id = 1 FOR UPDATE NOWAIT", connection) { Transaction = transaction };
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    return new Releaser(connection, transaction);
                }
                catch (OracleException ex) when (ex.Number == 54)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new MigrationException($"Timed out after {timeout.TotalSeconds:0}s waiting for the deploy lock ({table}). Another trek deploy is running against this schema.");
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Releaser(OracleConnection connection, OracleTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
