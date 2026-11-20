using Badger.Trek.Core.Providers;
using MySqlConnector;

namespace Badger.Trek.Providers.MySql;

public sealed class MySqlSession : IDatabaseSession
{
    private readonly MySqlConnection connection;
    private MySqlTransaction? transaction;

    public MySqlSession(MySqlConnection connection, string description)
    {
        this.connection = connection;
        Description = description;
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
            {
                Message?.Invoke(error.Message);
            }
        };
    }

    public event Action<string>? Message;

    public string Description { get; }

    public bool InTransaction => transaction is not null;

    internal MySqlConnection Connection => connection;

    /// <summary>The open transaction, which a command run on <see cref="Connection"/> must join.</summary>
    internal MySqlTransaction? Transaction => transaction;

    public TimeSpan? CommandTimeout { get; set; }

    public async Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already open.");
        }

        transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        var tx = transaction ?? throw new InvalidOperationException("No transaction is open.");
        transaction = null;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            if (tx.Connection is not null)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The script already ended the transaction: nothing to undo.
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

    private MySqlCommand CreateCommand(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        var command = new MySqlCommand(sql, connection, transaction);
        if (CommandTimeout is { } timeout)
        {
            command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds);
        }

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                // DATETIME columns hold UTC without an offset.
                var v = value switch
                {
                    DateTimeOffset d => d.UtcDateTime,
                    bool b => b ? 1 : 0,
                    _ => value,
                };
                command.Parameters.AddWithValue("@" + name.TrimStart('@'), v ?? DBNull.Value);
            }
        }

        return command;
    }
}
