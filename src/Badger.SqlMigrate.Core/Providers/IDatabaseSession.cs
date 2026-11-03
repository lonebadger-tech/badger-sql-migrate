namespace Badger.SqlMigrate.Core.Providers;

/// <summary>
/// A single open connection. Keeping this small (rather than exposing DbConnection) lets the engine be
/// tested with an in-memory fake and keeps driver types inside provider assemblies.
/// </summary>
public interface IDatabaseSession : IAsyncDisposable
{
    /// <summary>A short description for logs, e.g. "sqlserver://host:1433/db". Never includes secrets.</summary>
    string Description { get; }

    bool InTransaction { get; }

    /// <summary>Command timeout for subsequent commands; null = provider default.</summary>
    TimeSpan? CommandTimeout { get; set; }

    Task BeginTransactionAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);

    /// <summary>Executes one batch of SQL. Parameters use the provider's naming convention.</summary>
    Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken);

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken);

    /// <summary>Informational messages the server emitted (PRINT, RAISERROR with low severity, NOTICE).</summary>
    event Action<string>? Message;
}
