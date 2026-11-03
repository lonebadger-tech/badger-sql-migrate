using Badger.SqlMigrate.Core.Providers;
using Npgsql;

namespace Badger.SqlMigrate.Providers.PostgreSql;

public sealed class PostgreSqlSession : IDatabaseSession
{
    private readonly NpgsqlConnection connection;
    private NpgsqlTransaction? transaction;

    public PostgreSqlSession(NpgsqlConnection connection, string description)
    {
        this.connection = connection;
        Description = description;
        connection.Notice += (_, e) => Message?.Invoke(e.Notice.MessageText);
    }

    public event Action<string>? Message;

    public string Description { get; }

    public bool InTransaction => transaction is not null;

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
            // The script already ended the transaction (e.g. a Sqitch verify script's own ROLLBACK): nothing to undo.
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

    private NpgsqlCommand CreateCommand(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        if (CommandTimeout is { } timeout)
        {
            command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds);
        }

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                // timestamptz takes UTC values only.
                var v = value is DateTimeOffset d ? d.ToUniversalTime() : value;
                command.Parameters.Add(v is null
                    ? new NpgsqlParameter(name.TrimStart('@'), NpgsqlTypes.NpgsqlDbType.Text) { Value = DBNull.Value }
                    : new NpgsqlParameter(name.TrimStart('@'), v));
            }
        }

        return command;
    }
}
