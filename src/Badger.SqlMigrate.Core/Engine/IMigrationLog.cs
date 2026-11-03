namespace Badger.SqlMigrate.Core.Engine;

/// <summary>Where the engine reports progress. The CLI renders it; library users can plug in their logger.</summary>
public interface IMigrationLog
{
    void Info(string message);

    void Success(string message);

    void Warn(string message);

    /// <summary>Verbose detail such as server PRINT output and batch timings.</summary>
    void Detail(string message);
}

public sealed class NullMigrationLog : IMigrationLog
{
    public static readonly NullMigrationLog Instance = new();

    public void Info(string message)
    {
    }

    public void Success(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Detail(string message)
    {
    }
}

/// <summary>Who is running the tool; recorded with every change and event.</summary>
public sealed record Actor(string User, string Host, string ToolVersion)
{
    public static Actor Current(string toolVersion) => new(Environment.UserName, Environment.MachineName, toolVersion);
}
