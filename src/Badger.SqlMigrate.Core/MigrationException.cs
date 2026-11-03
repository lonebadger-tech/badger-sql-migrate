namespace Badger.SqlMigrate.Core;

/// <summary>An expected, user-facing failure (bad plan, hash mismatch, failed script). The CLI prints the message without a stack trace.</summary>
public class MigrationException : Exception
{
    public MigrationException(string message)
        : base(message)
    {
    }

    public MigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
