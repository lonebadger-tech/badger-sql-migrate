using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>Session-owned sp_getapplock so two deployers (devs, pipelines) can never interleave. Works on Azure SQL.</summary>
public sealed class SqlServerDeployLock : IDeployLock
{
    public async Task<IAsyncDisposable> AcquireAsync(IDatabaseSession session, string stateSchema, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var resource = $"badger:{stateSchema}";
        const string sql = """
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @timeout, @DbPrincipal = 'public';
            SELECT @result AS result;
            """;
        var previous = session.CommandTimeout;
        session.CommandTimeout = timeout + TimeSpan.FromSeconds(30);
        try
        {
            var rows = await session.QueryAsync(sql, new Dictionary<string, object?>
            {
                ["resource"] = resource,
                ["timeout"] = (int)timeout.TotalMilliseconds,
            }, cancellationToken).ConfigureAwait(false);
            var result = Convert.ToInt32(rows[0]["result"], System.Globalization.CultureInfo.InvariantCulture);
            if (result < 0)
            {
                throw new MigrationException(result == -1
                    ? $"Timed out after {timeout.TotalSeconds:0}s waiting for the deploy lock '{resource}'. Another bsm deploy is running against this database."
                    : $"Could not acquire the deploy lock '{resource}' (sp_getapplock returned {result}).");
            }
        }
        finally
        {
            session.CommandTimeout = previous;
        }

        return new Releaser(session, resource);
    }

    private sealed class Releaser(IDatabaseSession session, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await session.ExecuteAsync(
                "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session', @DbPrincipal = 'public';",
                new Dictionary<string, object?> { ["resource"] = resource },
                CancellationToken.None).ConfigureAwait(false);
        }
    }
}
