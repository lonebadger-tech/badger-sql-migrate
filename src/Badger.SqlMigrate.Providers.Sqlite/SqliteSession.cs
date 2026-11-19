using System.Globalization;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Badger.SqlMigrate.Providers.Sqlite;

public sealed class SqliteSession : IDatabaseSession
{
    private readonly SqliteConnection connection;
    private SqliteTransaction? transaction;

    public SqliteSession(SqliteConnection connection, string path)
    {
        this.connection = connection;
        Path = path;
        Description = "sqlite://" + path;
    }

    /// <summary>SQLite has no server notices.</summary>
    public event Action<string>? Message
    {
        add { }
        remove { }
    }

    public string Description { get; }

    /// <summary>Full path of the database file.</summary>
    public string Path { get; }

    public bool InTransaction => transaction is not null;

    public TimeSpan? CommandTimeout { get; set; }

    public Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already open.");
        }

        // BEGIN IMMEDIATE: take the write lock up front rather than failing halfway with SQLITE_BUSY.
        transaction = connection.BeginTransaction(deferred: false);
        return Task.CompletedTask;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        var tx = transaction ?? throw new InvalidOperationException("No transaction is open.");
        try
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            // Still open (e.g. deferred foreign keys violated): the caller rolls it back.
            throw new Core.MigrationException($"Commit failed on {Description}: {ex.Message}", ex);
        }

        transaction = null;
        await tx.DisposeAsync().ConfigureAwait(false);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        var tx = transaction;
        if (tx is null)
        {
            return;
        }

        transaction = null;
        try
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            // The script already ended the transaction (its own COMMIT or ROLLBACK): nothing to undo.
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        if (transaction is not null)
        {
            await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private SqliteCommand CreateCommand(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        if (CommandTimeout is { } timeout)
        {
            command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds);
        }

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name.StartsWith('@') ? name : "@" + name, value switch
                {
                    null => DBNull.Value,
                    DateTimeOffset d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind)).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    bool b => b ? 1L : 0L,
                    _ => value,
                });
            }
        }

        return command;
    }
}
