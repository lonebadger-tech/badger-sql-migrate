using System.Data;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer;

public sealed class SqlServerSession : IDatabaseSession
{
    private readonly SqlConnection connection;
    private SqlTransaction? transaction;

    public SqlServerSession(SqlConnection connection, string description, IReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider>? keyStores = null)
    {
        this.connection = connection;
        Description = description;
        KeyStores = keyStores;
        connection.InfoMessage += (_, e) => Message?.Invoke(e.Message);
    }

    public event Action<string>? Message;

    public string Description { get; }

    public bool InTransaction => transaction is not null;

    public TimeSpan? CommandTimeout { get; set; }

    internal SqlConnection Connection => connection;

    /// <summary>Always Encrypted key stores registered on this connection (used to provision keys).</summary>
    internal IReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider>? KeyStores { get; }

    /// <summary>How the schema fingerprint treats environment-managed details (partition boundaries).</summary>
    public SchemaFingerprintSettings Fingerprint { get; init; } = SchemaFingerprintSettings.Default;

    public async Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already open.");
        }

        transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
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
            // The server may already have rolled back (e.g. XACT_ABORT or severity >= 16 in some cases).
            if (tx.Connection is not null)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
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

    private SqlCommand CreateCommand(string sql, IReadOnlyDictionary<string, object?>? parameters)
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
                command.Parameters.Add(CreateParameter(name, value));
            }
        }

        return command;
    }

    /// <summary>Explicit types keep plans stable and are required for Always Encrypted parameter description.</summary>
    private static SqlParameter CreateParameter(string name, object? value)
    {
        var parameterName = name.StartsWith('@') ? name : "@" + name;
        return value switch
        {
            null => new SqlParameter(parameterName, SqlDbType.NVarChar, -1) { Value = DBNull.Value },
            string s => new SqlParameter(parameterName, SqlDbType.NVarChar, s.Length > 4000 ? -1 : 4000) { Value = s },
            long l => new SqlParameter(parameterName, SqlDbType.BigInt) { Value = l },
            int i => new SqlParameter(parameterName, SqlDbType.Int) { Value = i },
            bool b => new SqlParameter(parameterName, SqlDbType.Bit) { Value = b },
            DateTimeOffset d => new SqlParameter(parameterName, SqlDbType.DateTimeOffset) { Value = d },
            _ => new SqlParameter(parameterName, value),
        };
    }
}
