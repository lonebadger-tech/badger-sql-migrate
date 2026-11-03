using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.SqlServer;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

/// <summary>
/// An in-process key store standing in for an HSM or Key Vault. Loaded through the same
/// "keyStores.custom" configuration a user would write, so the assembly-loading path is exercised too.
/// </summary>
public sealed class TestKeyStoreProvider : SqlColumnEncryptionKeyStoreProvider
{
    public const string Name = "BSM_TEST_STORE";

    private static readonly ConcurrentDictionary<string, RSA> Keys = new(StringComparer.Ordinal);

    public override byte[] EncryptColumnEncryptionKey(string masterKeyPath, string encryptionAlgorithm, byte[] columnEncryptionKey) =>
        Key(masterKeyPath).Encrypt(columnEncryptionKey, RSAEncryptionPadding.OaepSHA1);

    public override byte[] DecryptColumnEncryptionKey(string masterKeyPath, string encryptionAlgorithm, byte[] encryptedColumnEncryptionKey) =>
        Key(masterKeyPath).Decrypt(encryptedColumnEncryptionKey, RSAEncryptionPadding.OaepSHA1);

    private static RSA Key(string path) => Keys.GetOrAdd(path, _ => RSA.Create(2048));
}

[Collection("sqlserver")]
public sealed class AlwaysEncryptedTests(SqlServerFixture fixture)
{
    private const string Ssn = "123-45-6789";
    private readonly SqlServerProvider provider = new();

    [SkippableFact]
    public async Task Keys_are_provisioned_per_environment_rotated_and_data_round_trips()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        var dbName = await CreateDatabaseAsync();
        using var project = new ProjectDir();
        project.Change("create-customer", """
            CREATE TABLE dbo.Customer (
                Id int NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
                Ssn char(11) COLLATE Latin1_General_BIN2
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK_Customer, ENCRYPTION_TYPE = Deterministic, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL);
            """, "DROP TABLE dbo.Customer;");

        // 1. First deploy provisions the CMK and CEK before the change that uses them.
        var env = Environment("test", dbName, cmks: [("CMK_2026", "test/cmk-2026")], cekCmk: "CMK_2026");
        await using (var e = await EngineAsync(env, project))
        {
            Assert.Equal(["create-customer"], (await e.Engine.DeployAsync(new DeployOptions())).Deployed);
            Assert.All(await e.Engine.PlanResourcesAsync(), a => Assert.Equal(ResourceActionKind.InSync, a.Kind));
        }

        await InsertAsync(env);
        Assert.Equal(Ssn, await ReadAsync(env));
        Assert.Equal(["CMK_2026"], await CekValuesAsync(dbName));

        // 2. Rotation: point the CEK at a new CMK. The deploy adds the new value; dropping the old one is destructive.
        var rotated = Environment("test", dbName, cmks: [("CMK_2026", "test/cmk-2026"), ("CMK_2027", "test/cmk-2027")], cekCmk: "CMK_2027");
        await using (var e = await EngineAsync(rotated, project))
        {
            var plan = await e.Engine.PlanResourcesAsync();
            Assert.Contains(plan, a => a.Kind == ResourceActionKind.Apply && a.Name == "CMK_2027");
            Assert.Contains(plan, a => a.Kind == ResourceActionKind.Apply && a.Name == "CEK_Customer");
            Assert.Contains(plan, a => a.Kind == ResourceActionKind.Destructive && a.Name == "CEK_Customer");

            await e.Engine.DeployAsync(new DeployOptions());
            Assert.Equal(["CMK_2026", "CMK_2027"], await CekValuesAsync(dbName));

            await e.Engine.ProvisionAsync(dryRun: false, allowDestructive: true);
            Assert.Equal(["CMK_2027"], await CekValuesAsync(dbName));
        }

        // Data encrypted before rotation still decrypts: the CEK itself did not change, only its protection.
        Assert.Equal(Ssn, await ReadAsync(rotated));

        // 3. A changed key path for an existing CMK name is a conflict, not a silent overwrite.
        var conflicting = Environment("test", dbName, cmks: [("CMK_2027", "test/somewhere-else")], cekCmk: "CMK_2027");
        await using (var e = await EngineAsync(conflicting, project))
        {
            var ex = await Assert.ThrowsAsync<MigrationException>(() => e.Engine.DeployAsync(new DeployOptions()));
            Assert.Contains("cannot be changed", ex.Message, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Environments_with_different_keys_have_identical_certificates_and_fingerprints()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        using var project = new ProjectDir();
        project.Change("create-customer", """
            CREATE TABLE dbo.Customer (
                Id int NOT NULL PRIMARY KEY,
                Ssn char(11) COLLATE Latin1_General_BIN2
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK_Customer, ENCRYPTION_TYPE = Randomized, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NULL);
            """, "DROP TABLE dbo.Customer;");

        var results = new List<VerifyResult>();
        foreach (var (envName, cmk, path) in new[] { ("test", "CMK_Test", "test/test-key"), ("prod", "CMK_Prod", "test/prod-key") })
        {
            var env = Environment(envName, await CreateDatabaseAsync(), cmks: [(cmk, path)], cekCmk: cmk);
            await using var e = await EngineAsync(env, project);
            await e.Engine.DeployAsync(new DeployOptions());
            results.Add(await e.Engine.VerifyAsync());
        }

        Assert.All(results, r => Assert.True(r.Ok, string.Join("; ", r.Problems)));
        Assert.Equal(results[0].Status.DatabaseHeadChainId, results[1].Status.DatabaseHeadChainId);
        Assert.Equal(results[0].LiveFingerprint, results[1].LiveFingerprint);
    }

    private DeploymentEnvironment Environment(string name, string database, (string Name, string Path)[] cmks, string cekCmk)
    {
        var masterKeys = new JsonObject();
        foreach (var (cmk, path) in cmks)
        {
            masterKeys[cmk] = new JsonObject { ["keyStore"] = TestKeyStoreProvider.Name, ["keyPath"] = path };
        }

        var builder = new SqlConnectionStringBuilder(fixture.MasterConnectionString) { InitialCatalog = database };
        return new DeploymentEnvironment
        {
            Name = name,
            ConnectionString = builder.ConnectionString,
            ProviderSettings = new JsonObject
            {
                ["alwaysEncrypted"] = new JsonObject
                {
                    ["keyStores"] = new JsonObject
                    {
                        ["custom"] = new JsonArray(new JsonObject
                        {
                            ["name"] = TestKeyStoreProvider.Name,
                            ["assembly"] = Path.GetFileName(typeof(TestKeyStoreProvider).Assembly.Location),
                            ["type"] = typeof(TestKeyStoreProvider).FullName,
                        }),
                    },
                    ["columnMasterKeys"] = masterKeys,
                    ["columnEncryptionKeys"] = new JsonObject { ["CEK_Customer"] = new JsonObject { ["columnMasterKey"] = cekCmk } },
                },
            },
        };
    }

    private static SessionContext Context => new() { BaseDirectory = AppContext.BaseDirectory };

    private async Task<(MigrationEngine Engine, IDatabaseSession Session)> OpenAsync(DeploymentEnvironment env, ProjectDir project)
    {
        Assert.Empty(provider.ValidateEnvironment(env));
        var session = await provider.OpenSessionAsync(env, Context, CancellationToken.None);
        return (new MigrationEngine(MigrationProject.Load(project.Root, provider.Id, env.Name), provider, env, session, new Actor("test", "test", "0")), session);
    }

    private async Task<Handle> EngineAsync(DeploymentEnvironment env, ProjectDir project)
    {
        var (engine, session) = await OpenAsync(env, project);
        return new Handle(engine, session);
    }

    private static async Task InsertAsync(DeploymentEnvironment env)
    {
        var (connection, _, _) = SqlServerConnectionFactory.Create(env, Context);
        await using (connection)
        {
            await connection.OpenAsync();
            await using var insert = new SqlCommand("INSERT INTO dbo.Customer (Id, Ssn) VALUES (@id, @ssn);", connection);
            insert.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = 1 });
            insert.Parameters.Add(new SqlParameter("@ssn", SqlDbType.Char, 11) { Value = Ssn });
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> ReadAsync(DeploymentEnvironment env)
    {
        var (connection, _, _) = SqlServerConnectionFactory.Create(env, Context);
        await using (connection)
        {
            await connection.OpenAsync();
            await using var select = new SqlCommand("SELECT Ssn FROM dbo.Customer WHERE Id = 1;", connection);
            return (string)(await select.ExecuteScalarAsync())!;
        }
    }

    private async Task<List<string>> CekValuesAsync(string database)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(fixture.MasterConnectionString) { InitialCatalog = database }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT m.name FROM sys.column_encryption_key_values v
            JOIN sys.column_master_keys m ON m.column_master_key_id = v.column_master_key_id ORDER BY m.name;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var name = "bsm_ae_" + Guid.NewGuid().ToString("N")[..12];
        await using var master = new SqlConnection(fixture.MasterConnectionString);
        await master.OpenAsync();
        await using var create = new SqlCommand($"CREATE DATABASE [{name}];", master);
        await create.ExecuteNonQueryAsync();
        return name;
    }

    private sealed record Handle(MigrationEngine Engine, IDatabaseSession Session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private sealed class ProjectDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bsm-ae", Guid.NewGuid().ToString("N"));

        public ProjectDir()
        {
            Directory.CreateDirectory(Path.Combine(Root, MigrationProject.ChangesFolder));
            File.WriteAllText(Path.Combine(Root, "bsm.plan"), string.Empty);
        }

        public void Change(string name, string deploy, string revert)
        {
            var dir = Path.Combine(Root, MigrationProject.ChangesFolder, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MigrationProject.DeployFile), deploy);
            File.WriteAllText(Path.Combine(dir, MigrationProject.RevertFile), revert);
            File.AppendAllText(Path.Combine(Root, "bsm.plan"), name + "\n");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
