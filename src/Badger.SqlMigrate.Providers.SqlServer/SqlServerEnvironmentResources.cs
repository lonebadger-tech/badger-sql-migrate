using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>
/// Provisions Always Encrypted keys per environment from the "alwaysEncrypted:" block:
/// <list type="bullet">
/// <item>column master key metadata (CREATE COLUMN MASTER KEY, signed when enclave-enabled);</item>
/// <item>column encryption keys: a new random 256-bit key encrypted with the environment's CMK;</item>
/// <item>rotation: when a CEK is pointed at a new CMK, its key is decrypted with the old CMK and re-encrypted
/// (ADD VALUE); dropping the old value is a separate, destructive step.</item>
/// </list>
/// Key material never leaves the client unencrypted and is never logged. CMK metadata cannot be altered in
/// SQL Server, so a changed key path is reported as a conflict: add a new CMK name and rotate instead.
/// </summary>
public sealed class SqlServerEnvironmentResources : IEnvironmentResources
{
    private const string CmkType = "column master key";
    private const string CekType = "column encryption key";
    private const string Algorithm = "RSA_OAEP";

    public IReadOnlyList<string> Validate(DeploymentEnvironment environment)
    {
        var problems = new List<string>();
        foreach (var unknown in AlwaysEncryptedSettings.UnknownSections(environment.ProviderSettings))
        {
            problems.Add($"environment '{environment.Name}': unknown SQL Server setting '{unknown}'. Known: {AlwaysEncryptedSettings.SectionName}.");
        }

        AlwaysEncryptedSettings? settings;
        try
        {
            settings = AlwaysEncryptedSettings.From(environment);
        }
        catch (MigrationException ex)
        {
            problems.Add(ex.Message);
            return problems;
        }

        if (settings is null)
        {
            return problems;
        }

        foreach (var (name, cmk) in settings.ColumnMasterKeys)
        {
            if (string.IsNullOrWhiteSpace(cmk.KeyPath))
            {
                problems.Add($"environment '{environment.Name}': column master key '{name}' needs keyPath.");
            }
        }

        foreach (var (name, cek) in settings.ColumnEncryptionKeys)
        {
            if (!settings.ColumnMasterKeys.ContainsKey(cek.ColumnMasterKey))
            {
                problems.Add($"environment '{environment.Name}': column encryption key '{name}' uses column master key '{cek.ColumnMasterKey}', which is not defined under columnMasterKeys.");
            }
        }

        if (settings.Attestation is { } attestation && !Enum.TryParse<SqlConnectionAttestationProtocol>(attestation.Protocol, true, out _))
        {
            problems.Add($"environment '{environment.Name}': unknown attestation protocol '{attestation.Protocol}' (AAS, HGS, None).");
        }

        return problems;
    }

    public async Task<IReadOnlyList<ResourceAction>> PlanAsync(IDatabaseSession session, DeploymentEnvironment environment, CancellationToken cancellationToken)
    {
        var settings = AlwaysEncryptedSettings.From(environment);
        if (settings is null || (settings.ColumnMasterKeys.Count == 0 && settings.ColumnEncryptionKeys.Count == 0))
        {
            return [];
        }

        var dbCmks = (await session.QueryAsync(
            "SELECT name, key_store_provider_name, key_path, allow_enclave_computations FROM sys.column_master_keys;", null, cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => (string)r["name"]!, StringComparer.Ordinal);
        var dbCekValues = (await session.QueryAsync("""
            SELECT k.name AS cek, m.name AS cmk
            FROM sys.column_encryption_keys k
            JOIN sys.column_encryption_key_values v ON v.column_encryption_key_id = k.column_encryption_key_id
            JOIN sys.column_master_keys m ON m.column_master_key_id = v.column_master_key_id;
            """, null, cancellationToken).ConfigureAwait(false))
            .GroupBy(r => (string)r["cek"]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => (string)r["cmk"]!).ToList(), StringComparer.Ordinal);

        var actions = new List<ResourceAction>();
        foreach (var (name, cmk) in settings.ColumnMasterKeys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var store = KeyStoreProviders.Normalize(cmk.KeyStore);
            if (!dbCmks.TryGetValue(name, out var row))
            {
                actions.Add(new(ResourceActionKind.Apply, CmkType, name, $"create {CmkType} in {store}{(cmk.EnclaveComputations ? " (enclave-enabled)" : string.Empty)}"));
                continue;
            }

            var dbStore = (string)row["key_store_provider_name"]!;
            var dbPath = (string)row["key_path"]!;
            var dbEnclave = Convert.ToBoolean(row["allow_enclave_computations"], CultureInfo.InvariantCulture);
            if (!string.Equals(dbStore, store, StringComparison.OrdinalIgnoreCase) || !string.Equals(dbPath, cmk.KeyPath, StringComparison.OrdinalIgnoreCase) || dbEnclave != cmk.EnclaveComputations)
            {
                actions.Add(new(ResourceActionKind.Conflict, CmkType, name,
                    $"database has {dbStore} '{dbPath}'{(dbEnclave ? " (enclave)" : string.Empty)} but this environment configures {store} '{cmk.KeyPath}'{(cmk.EnclaveComputations ? " (enclave)" : string.Empty)}. " +
                    "Column master key metadata cannot be changed; define a new column master key name and point the column encryption keys at it to rotate."));
            }
            else
            {
                actions.Add(new(ResourceActionKind.InSync, CmkType, name, $"{store} key path matches"));
            }
        }

        foreach (var (name, cek) in settings.ColumnEncryptionKeys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!dbCekValues.TryGetValue(name, out var protectedBy))
            {
                actions.Add(new(ResourceActionKind.Apply, CekType, name, $"create {CekType} (new random key protected by {cek.ColumnMasterKey})", Payload(new CekPayload("create", cek.ColumnMasterKey, null))));
                continue;
            }

            if (!protectedBy.Contains(cek.ColumnMasterKey, StringComparer.Ordinal))
            {
                actions.Add(new(ResourceActionKind.Apply, CekType, name, $"add value protected by {cek.ColumnMasterKey} (rotation from {string.Join(", ", protectedBy)})", Payload(new CekPayload("add", cek.ColumnMasterKey, protectedBy[0]))));
            }
            else
            {
                actions.Add(new(ResourceActionKind.InSync, CekType, name, $"protected by {cek.ColumnMasterKey}"));
            }

            foreach (var old in protectedBy.Where(c => !string.Equals(c, cek.ColumnMasterKey, StringComparison.Ordinal)))
            {
                actions.Add(new(ResourceActionKind.Destructive, CekType, name, $"drop value protected by old column master key {old} (rotation complete)", Payload(new CekPayload("drop", old, null))));
            }
        }

        var referenced = dbCekValues.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);
        foreach (var name in dbCmks.Keys.Where(n => !settings.ColumnMasterKeys.ContainsKey(n)).Order(StringComparer.Ordinal))
        {
            actions.Add(new(ResourceActionKind.Info, CmkType, name, referenced.Contains(name)
                ? "exists in the database but not in this environment's configuration"
                : "exists in the database, is not configured and protects no column encryption key; drop it manually when no longer needed"));
        }

        foreach (var name in dbCekValues.Keys.Where(n => !settings.ColumnEncryptionKeys.ContainsKey(n)).Order(StringComparer.Ordinal))
        {
            actions.Add(new(ResourceActionKind.Info, CekType, name, "exists in the database but not in this environment's configuration"));
        }

        return actions;
    }

    public async Task ApplyAsync(IDatabaseSession session, DeploymentEnvironment environment, IReadOnlyList<ResourceAction> actions, CancellationToken cancellationToken)
    {
        var settings = AlwaysEncryptedSettings.From(environment) ?? throw new MigrationException("No alwaysEncrypted settings.");
        var stores = ((SqlServerSession)session).KeyStores
            ?? throw new MigrationException("The session was opened without Always Encrypted key stores.");

        foreach (var action in actions)
        {
            switch (action.ResourceType)
            {
                case CmkType:
                    await CreateCmkAsync(session, stores, action.Name, settings.ColumnMasterKeys[action.Name], cancellationToken).ConfigureAwait(false);
                    break;
                case CekType:
                    var payload = JsonSerializer.Deserialize<CekPayload>(action.Payload!)!;
                    switch (payload.Operation)
                    {
                        case "create":
                            await CreateCekAsync(session, stores, settings, action.Name, payload.ColumnMasterKey, cancellationToken).ConfigureAwait(false);
                            break;
                        case "add":
                            await AddCekValueAsync(session, stores, settings, action.Name, payload.ColumnMasterKey, payload.FromColumnMasterKey!, cancellationToken).ConfigureAwait(false);
                            break;
                        case "drop":
                            await session.ExecuteAsync($"ALTER COLUMN ENCRYPTION KEY {Quote(action.Name)} DROP VALUE (COLUMN_MASTER_KEY = {Quote(payload.ColumnMasterKey)});", null, cancellationToken).ConfigureAwait(false);
                            break;
                    }

                    break;
            }
        }
    }

    private static async Task CreateCmkAsync(IDatabaseSession session, IReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider> stores, string name, ColumnMasterKeySettings cmk, CancellationToken cancellationToken)
    {
        var store = KeyStoreProviders.Normalize(cmk.KeyStore);
        var enclave = string.Empty;
        if (cmk.EnclaveComputations)
        {
            var provider = KeyStoreProviders.Get(stores, store);
            var signature = await provider.SignColumnMasterKeyMetadataAsync(cmk.KeyPath, true, cancellationToken).ConfigureAwait(false);
            enclave = $", ENCLAVE_COMPUTATIONS (SIGNATURE = 0x{Convert.ToHexString(signature)})";
        }

        await session.ExecuteAsync(
            $"CREATE COLUMN MASTER KEY {Quote(name)} WITH (KEY_STORE_PROVIDER_NAME = {Literal(store)}, KEY_PATH = {Literal(cmk.KeyPath)}{enclave});",
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateCekAsync(IDatabaseSession session, IReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider> stores, AlwaysEncryptedSettings settings, string name, string cmkName, CancellationToken cancellationToken)
    {
        var cmk = settings.ColumnMasterKeys[cmkName];
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var encrypted = await KeyStoreProviders.Get(stores, cmk.KeyStore).EncryptColumnEncryptionKeyAsync(cmk.KeyPath, Algorithm, key, cancellationToken).ConfigureAwait(false);
            await session.ExecuteAsync(
                $"CREATE COLUMN ENCRYPTION KEY {Quote(name)} WITH VALUES (COLUMN_MASTER_KEY = {Quote(cmkName)}, ALGORITHM = '{Algorithm}', ENCRYPTED_VALUE = 0x{Convert.ToHexString(encrypted)});",
                null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task AddCekValueAsync(IDatabaseSession session, IReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider> stores, AlwaysEncryptedSettings settings, string name, string newCmkName, string oldCmkName, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync("""
            SELECT v.encrypted_value, v.encryption_algorithm_name, m.key_store_provider_name, m.key_path
            FROM sys.column_encryption_keys k
            JOIN sys.column_encryption_key_values v ON v.column_encryption_key_id = k.column_encryption_key_id
            JOIN sys.column_master_keys m ON m.column_master_key_id = v.column_master_key_id
            WHERE k.name = @cek AND m.name = @cmk;
            """, new Dictionary<string, object?> { ["cek"] = name, ["cmk"] = oldCmkName }, cancellationToken).ConfigureAwait(false);
        var row = rows.Count == 1 ? rows[0] : throw new MigrationException($"{CekType} {name}: no value protected by {oldCmkName}.");

        var oldProvider = KeyStoreProviders.Get(stores, (string)row["key_store_provider_name"]!);
        var key = await oldProvider.DecryptColumnEncryptionKeyAsync((string)row["key_path"]!, (string)row["encryption_algorithm_name"]!, (byte[])row["encrypted_value"]!, cancellationToken).ConfigureAwait(false);
        try
        {
            var cmk = settings.ColumnMasterKeys[newCmkName];
            var encrypted = await KeyStoreProviders.Get(stores, cmk.KeyStore).EncryptColumnEncryptionKeyAsync(cmk.KeyPath, Algorithm, key, cancellationToken).ConfigureAwait(false);
            await session.ExecuteAsync(
                $"ALTER COLUMN ENCRYPTION KEY {Quote(name)} ADD VALUE (COLUMN_MASTER_KEY = {Quote(newCmkName)}, ALGORITHM = '{Algorithm}', ENCRYPTED_VALUE = 0x{Convert.ToHexString(encrypted)});",
                null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string Payload(CekPayload payload) => JsonSerializer.Serialize(payload);

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string Literal(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private sealed record CekPayload(string Operation, string ColumnMasterKey, string? FromColumnMasterKey);
}
