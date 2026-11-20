using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Badger.Trek.Providers.Sqlite.Tests;

/// <summary>
/// TREK_SQLITE_LIBRARY (read by trek itself) runs the tests against that SQLite library (a path to libsqlite3.so.0,
/// sqlite3.dll or libsqlite3.dylib) instead of the engine trek ships with; CI uses it to test the oldest supported version
/// (3.26) and the versions common Linux distributions ship. TREK_TEST_SQLITE_VERSION, when set, is the version the
/// engine must report, so a run that quietly used another library fails.
/// </summary>
internal static class SystemSqlite
{
    public static string? ExpectedVersion => Environment.GetEnvironmentVariable("TREK_TEST_SQLITE_VERSION") is { Length: > 0 } version ? version : null;

    /// <summary>trek picks its SQLite engine when its provider assembly loads; make sure that happens before any test connects.</summary>
    [ModuleInitializer]
    internal static void LoadTheProvider() => RuntimeHelpers.RunModuleConstructor(typeof(SqliteProvider).Module.ModuleHandle);

    /// <summary>The version of the engine the tests run on.</summary>
    public static Version EngineVersion { get; } = ReadEngineVersion();

    private static Version ReadEngineVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Version.Parse((string)command.ExecuteScalar()!);
    }
}

public sealed class SystemSqliteTests
{
    [Fact]
    public void Tests_use_the_requested_sqlite_library()
    {
        var version = SystemSqlite.EngineVersion.ToString();
        if (SystemSqlite.ExpectedVersion is { } expected)
        {
            Assert.Equal(expected, version);
        }
        else
        {
            Assert.True(SystemSqlite.EngineVersion >= SqliteOptions.Oldest, $"SQLite {version} is older than trek supports");
        }
    }
}
