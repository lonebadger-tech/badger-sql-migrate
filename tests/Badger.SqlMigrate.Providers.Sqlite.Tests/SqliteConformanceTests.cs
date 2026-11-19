using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.Testing;

namespace Badger.SqlMigrate.Providers.Sqlite.Tests;

/// <summary>A database file in its own temp folder, deleted on dispose. SQLite needs no server, so these always run.</summary>
public sealed class TestDatabase : IConformanceDatabase
{
    private readonly string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bsm_sqlite_test_" + Guid.NewGuid().ToString("N")[..12]);

    public TestDatabase()
    {
        Directory.CreateDirectory(folder);
        Path = System.IO.Path.Combine(folder, "test.db");
        Profile = new DeploymentEnvironment { Name = "conformance", Database = Path, LockTimeoutSeconds = 5 };
    }

    public string Path { get; }

    public DeploymentEnvironment Profile { get; }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS to clean up.
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class SqliteConformanceTests : ProviderConformanceTests
{
    protected override IDatabaseProvider Provider { get; } = new SqliteProvider();

    protected override IConformanceDialect Dialect { get; } = new SqliteDialect();

    protected override Task<IConformanceDatabase?> CreateDatabaseAsync() => Task.FromResult<IConformanceDatabase?>(new TestDatabase());

    private sealed class SqliteDialect : IConformanceDialect
    {
        // 1 / 0 is NULL in SQLite, not an error.
        public string Failing => "SELECT * FROM no_such_table;";

        public string CreateTable(string name) => $"CREATE TABLE main.{name} (id INTEGER NOT NULL PRIMARY KEY, created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);";

        public string DropTable(string name) => $"DROP TABLE main.{name};";

        public string Repeatable(string name, int value) => $"DROP VIEW IF EXISTS main.{name};\nCREATE VIEW main.{name} AS SELECT {value} AS value;";

        public string VerifyTableExists(string name) => $"SELECT 1 FROM main.{name} LIMIT 0;";
    }
}
