using System.Text.Json.Nodes;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.SqlServer;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

public class ConnectionFactoryTests
{
    private static readonly SessionContext WithTokens = new() { Tokens = new FixedTokens() };

    private static SqlConnectionStringBuilder Build(DeploymentEnvironment environment, SessionContext? context = null)
    {
        var (connection, _, _) = SqlServerConnectionFactory.Create(environment, context ?? SessionContext.Empty);
        using (connection)
        {
            return new SqlConnectionStringBuilder(connection.ConnectionString);
        }
    }

    [Fact]
    public void Password_auth_with_strict_tls()
    {
        var b = Build(new DeploymentEnvironment
        {
            Name = "p",
            Host = "db.example.com",
            Port = 1444,
            Database = "App",
            Authentication = AuthenticationKind.Password,
            User = "deploy",
            Password = "secret",
            Tls = new TlsOptions { Mode = TlsMode.Strict, HostNameInCertificate = "db.example.com" },
        });

        Assert.Equal("db.example.com,1444", b.DataSource);
        Assert.Equal("App", b.InitialCatalog);
        Assert.Equal("deploy", b.UserID);
        Assert.Equal(SqlConnectionEncryptOption.Strict, b.Encrypt);
        Assert.Equal("Badger SQL Migrate", b.ApplicationName);
    }

    [Fact]
    public void Integrated_auth_sets_integrated_security()
    {
        var b = Build(new DeploymentEnvironment { Name = "p", Host = "sql01", Database = "App", Authentication = AuthenticationKind.Integrated });
        Assert.True(b.IntegratedSecurity);
    }

    [Theory]
    [InlineData(AuthenticationKind.EntraDefault)]
    [InlineData(AuthenticationKind.EntraInteractive)]
    [InlineData(AuthenticationKind.EntraDeviceCode)]
    [InlineData(AuthenticationKind.EntraManagedIdentity)]
    [InlineData(AuthenticationKind.EntraServicePrincipal)]
    [InlineData(AuthenticationKind.EntraServicePrincipalCertificate)]
    [InlineData(AuthenticationKind.EntraWorkloadIdentity)]
    [InlineData(AuthenticationKind.EntraAzureCli)]
    [InlineData(AuthenticationKind.EntraAzurePowerShell)]
    [InlineData(AuthenticationKind.EntraAzureDeveloperCli)]
    [InlineData(AuthenticationKind.EntraAzurePipelines)]
    [InlineData(AuthenticationKind.EntraEnvironment)]
    [InlineData(AuthenticationKind.EntraAccessToken)]
    public void Entra_token_flows_use_the_token_callback_and_strip_passwords(AuthenticationKind kind)
    {
        var environment = new DeploymentEnvironment
        {
            Name = "p",
            ConnectionString = "Server=x.database.windows.net;Database=App;Authentication=Active Directory Default;Password=leftover",
            Authentication = kind,
        };
        var (connection, _, _) = SqlServerConnectionFactory.Create(environment, WithTokens);
        using (connection)
        {
            var b = new SqlConnectionStringBuilder(connection.ConnectionString);
            Assert.NotNull(connection.AccessTokenCallback);
            Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
            Assert.Equal(string.Empty, b.Password);
        }
    }

    [Fact]
    public void Token_flow_without_a_token_source_is_a_clear_error()
    {
        var ex = Assert.Throws<MigrationException>(() => Build(new DeploymentEnvironment { Name = "p", Host = "x", Authentication = AuthenticationKind.EntraInteractive }));
        Assert.Contains("token source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entra_integrated_and_password_use_native_driver_modes()
    {
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryIntegrated, Build(new DeploymentEnvironment { Name = "p", Host = "x", Authentication = AuthenticationKind.EntraIntegrated }).Authentication);

#pragma warning disable CS0618
        var expected = SqlAuthenticationMethod.ActiveDirectoryPassword;
#pragma warning restore CS0618
        var warnings = new List<string>();
        var b = Build(new DeploymentEnvironment { Name = "p", Host = "x", Authentication = AuthenticationKind.EntraPassword, User = "u@contoso.com", Password = "pw" }, new SessionContext { Warn = warnings.Add });
        Assert.Equal(expected, b.Authentication);
        Assert.Contains(warnings, w => w.Contains("MFA", StringComparison.Ordinal));
    }

    [Fact]
    public void Any_sqlclient_keyword_can_be_set_and_wins()
    {
        var b = Build(new DeploymentEnvironment
        {
            Name = "p",
            Host = "x.database.windows.net",
            Database = "App",
            Authentication = AuthenticationKind.Integrated,
            ConnectionProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ApplicationIntent"] = "ReadOnly",
                ["Multi Subnet Failover"] = "true",
                ["Connect Retry Count"] = "5",
                ["Command Timeout"] = "120",
                ["Max Pool Size"] = "7",
                ["Server SPN"] = "MSSQLSvc/x:1433",
                ["IP Address Preference"] = "IPv6First",
                ["Application Name"] = "release-pipeline",
            },
        });

        Assert.Equal(ApplicationIntent.ReadOnly, b.ApplicationIntent);
        Assert.True(b.MultiSubnetFailover);
        Assert.Equal(5, b.ConnectRetryCount);
        Assert.Equal(120, b.CommandTimeout);
        Assert.Equal(7, b.MaxPoolSize);
        Assert.Equal("MSSQLSvc/x:1433", b.ServerSPN);
        Assert.Equal(SqlConnectionIPAddressPreference.IPv6First, b.IPAddressPreference);
        Assert.Equal("release-pipeline", b.ApplicationName);
    }

    [Fact]
    public void Always_encrypted_block_enables_column_encryption_and_attestation()
    {
        var b = Build(new DeploymentEnvironment
        {
            Name = "p",
            Host = "x.database.windows.net",
            Database = "App",
            Authentication = AuthenticationKind.Password,
            User = "u",
            ProviderSettings = (JsonObject)JsonNode.Parse("""
                { "alwaysEncrypted": { "attestation": { "protocol": "AAS", "url": "https://attest.example/attest/SgxEnclave" },
                  "keyStores": { "custom": [] } } }
                """)!,
        });

        Assert.Equal(SqlConnectionColumnEncryptionSetting.Enabled, b.ColumnEncryptionSetting);
        Assert.Equal(SqlConnectionAttestationProtocol.AAS, b.AttestationProtocol);
        Assert.Equal("https://attest.example/attest/SgxEnclave", b.EnclaveAttestationUrl);
    }

    [Fact]
    public void Ssh_tunnel_rewrites_endpoint_and_keeps_tls_host_name()
    {
        var b = Build(
            new DeploymentEnvironment { Name = "p", Host = "db.internal", Database = "App", Authentication = AuthenticationKind.Integrated },
            new SessionContext { EndpointOverride = new Endpoint("127.0.0.1", 50123) });

        Assert.Equal("127.0.0.1,50123", b.DataSource);
        Assert.Equal("db.internal", b.HostNameInCertificate);
    }

    [Fact]
    public void Raw_connection_string_is_respected_and_endpoint_parsed()
    {
        var environment = new DeploymentEnvironment { Name = "p", ConnectionString = "Server=tcp:sql.example.com,1500;Database=App;Integrated Security=true" };
        Assert.Equal(new Endpoint("sql.example.com", 1500), SqlServerConnectionFactory.ResolveEndpoint(environment));
        Assert.Equal("App", Build(environment).InitialCatalog);
    }

    [Fact]
    public void Validation_reports_bad_connection_properties_missing_fields_and_unknown_settings()
    {
        var provider = new SqlServerProvider();
        var problems = provider.ValidateEnvironment(new DeploymentEnvironment
        {
            Name = "p",
            Authentication = AuthenticationKind.EntraServicePrincipal,
            ConnectionProperties = new Dictionary<string, string> { ["Colunm Encryption Setting"] = "Enabled" },
            ProviderSettings = (JsonObject)JsonNode.Parse("""
                { "alwaysEncryptd": {},
                  "alwaysEncrypted": { "columnEncryptionKeys": { "CEK1": { "columnMasterKey": "Missing" } } } }
                """)!,
        });

        Assert.Contains(problems, p => p.Contains("connectionProperties 'Colunm Encryption Setting'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'connectionString' or 'host'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("entraServicePrincipal requires", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("unknown SQL Server setting 'alwaysEncryptd'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'Missing', which is not defined", StringComparison.Ordinal));
    }

    [Fact]
    public void Certificate_store_keys_fail_clearly_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var ex = Assert.Throws<MigrationException>(() => KeyStoreProviders.Get(new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>(), "certificateStore"));
        Assert.Contains("only available on Windows", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void System_key_stores_are_never_registered_on_the_connection()
    {
        // SqlClient rejects MSSQL_* names on a connection; it always supplies those stores itself.
        var all = new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>(StringComparer.OrdinalIgnoreCase)
        {
            [KeyStoreProviders.CertificateStore] = new SqlColumnEncryptionCertificateStoreProvider(),
            [KeyStoreProviders.Cng] = new SqlColumnEncryptionCngProvider(),
            [KeyStoreProviders.Csp] = new SqlColumnEncryptionCspProvider(),
            ["HSM_VAULT"] = new SqlColumnEncryptionCertificateStoreProvider(),
        };

        var onConnection = KeyStoreProviders.ForConnection(all);

        Assert.Equal(["HSM_VAULT"], onConnection.Keys);
        using var rejected = new SqlConnection();
        Assert.Throws<ArgumentException>(() => rejected.RegisterColumnEncryptionKeyStoreProvidersOnConnection(all));
        using var connection = new SqlConnection();
        connection.RegisterColumnEncryptionKeyStoreProvidersOnConnection(onConnection);
    }

    private sealed class FixedTokens : IAccessTokenProvider
    {
        public string DefaultScope => "https://database.windows.net/.default";

        public Task<AccessTokenResult> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken) =>
            Task.FromResult(new AccessTokenResult("token", DateTimeOffset.UtcNow.AddHours(1)));
    }
}
