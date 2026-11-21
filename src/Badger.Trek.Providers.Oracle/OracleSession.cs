using System.Data;
using Badger.Trek.Core.Providers;
using Oracle.ManagedDataAccess.Client;

namespace Badger.Trek.Providers.Oracle;

/// <summary>
/// One ODP.NET connection. Parameters bind by name (:name in SQL, plain names in the dictionary); strings longer than a
/// VARCHAR2 can hold bind as CLOB; DateTimeOffset binds as a UTC TIMESTAMP and bool as 1/0. LONG columns (view text,
/// defaults) are read whole.
/// </summary>
public sealed class OracleSession : IDatabaseSession
{
    /// <summary>Longest string bound as VARCHAR2; longer ones bind as CLOB.</summary>
    private const int MaxVarchar = 2000;

    private readonly OracleConnection connection;
    private OracleTransaction? transaction;

    public OracleSession(OracleConnection connection, string description, string schema, string connectionString)
    {
        this.connection = connection;
        Description = description;
        Schema = schema;
        ConnectionString = connectionString;
        connection.InfoMessage += (_, e) => Message?.Invoke(e.Message);
    }

    public event Action<string>? Message;

    public string Description { get; }

    /// <summary>The schema trek deploys to and keeps its journal in (upper case unless created with a quoted name).</summary>
    public string Schema { get; }

    /// <summary>For a second connection to the same schema (the deploy lock).</summary>
    internal string ConnectionString { get; }

    public bool InTransaction => transaction is not null;

    internal OracleConnection Connection => connection;

    internal OracleTransaction? Transaction => transaction;

    public TimeSpan? CommandTimeout { get; set; }

    public Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already open.");
        }

        transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        return Task.CompletedTask;
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
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
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

    private OracleCommand CreateCommand(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        var command = new OracleCommand(sql, connection)
        {
            BindByName = true,
            InitialLONGFetchSize = -1,
            InitialLOBFetchSize = -1,
        };
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        if (CommandTimeout is { } timeout)
        {
            command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds);
        }

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.Add(Parameter(name.TrimStart(':', '@'), value));
            }
        }

        return command;
    }

    private static OracleParameter Parameter(string name, object? value) => value switch
    {
        null => new OracleParameter(name, OracleDbType.Varchar2) { Value = DBNull.Value },
        string s when s.Length > MaxVarchar => new OracleParameter(name, OracleDbType.Clob) { Value = s },
        string s => new OracleParameter(name, OracleDbType.Varchar2) { Value = s },
        DateTimeOffset d => new OracleParameter(name, OracleDbType.TimeStamp) { Value = d.UtcDateTime },
        DateTime d => new OracleParameter(name, OracleDbType.TimeStamp) { Value = d },
        bool b => new OracleParameter(name, OracleDbType.Int32) { Value = b ? 1 : 0 },
        int i => new OracleParameter(name, OracleDbType.Int32) { Value = i },
        long l => new OracleParameter(name, OracleDbType.Int64) { Value = l },
        _ => new OracleParameter(name, value),
    };
}
