using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Badger.SqlMigrate.Providers.Sqlite.Tests;

/// <summary>
/// BSM_TEST_SQLITE_LIBRARY runs the tests against that SQLite library (a path to libsqlite3.so.0, sqlite3.dll or
/// libsqlite3.dylib) instead of the engine bsm ships with; CI uses it to test the oldest supported version (3.26) and the
/// versions common Linux distributions ship. BSM_TEST_SQLITE_VERSION, when set, is the version the library must report.
/// </summary>
internal static class SystemSqlite
{
    public static string? Library => Environment.GetEnvironmentVariable("BSM_TEST_SQLITE_LIBRARY") is { Length: > 0 } path ? path : null;

    public static string? ExpectedVersion => Environment.GetEnvironmentVariable("BSM_TEST_SQLITE_VERSION") is { Length: > 0 } version ? version : null;

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

    [ModuleInitializer]
    internal static void Use()
    {
        if (Library is not { } library)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly, (name, _, _) =>
            name == "sqlite3" ? NativeLibrary.Load(library) : IntPtr.Zero);

        // Microsoft.Data.Sqlite installs its bundled engine the first time it is used; replace it before any connection opens.
        RuntimeHelpers.RunClassConstructor(typeof(SqliteConnection).TypeHandle);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
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
            Assert.True(SystemSqlite.EngineVersion >= SqliteOptions.Oldest, $"SQLite {version} is older than bsm supports");
        }
    }
}
