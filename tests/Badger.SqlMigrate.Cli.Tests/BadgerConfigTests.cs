using Badger.SqlMigrate.Cli;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Preprocessing;

namespace Badger.SqlMigrate.Cli.Tests;

public sealed class BadgerConfigTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "badger-config", Guid.NewGuid().ToString("N"));

    public BadgerConfigTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    private BadgerConfig Load(string yaml)
    {
        File.WriteAllText(Path.Combine(dir, BadgerConfig.FileName), yaml);
        return BadgerConfig.Load(dir);
    }

    private const string Sample = """
        provider: sqlserver
        defaultEnvironment: dev
        preprocessor: { unknown: keep, sqlcmdSyntax: false }
        variables:
          AppRole: app_user
          schemas:                       # block style: braces are fine unquoted
            calc: calc_{env}
          quoted: { stage: "stage_{env}" }   # inline maps need quotes around {..}
        environments:
          _azure:
            database: App
            authentication: entraInteractive
            tls: { mode: strict }
            entra: { tenantId: tenant-1, tokenCache: persistent }
            connectionProperties: { Connect Retry Count: 3, Command Timeout: 60 }
          dev:
            extends: _azure
            host: dev.database.windows.net
            entra: { loginHint: dev@contoso.com }
            variables: { AppRole: dev_role }
          prod:
            extends: _azure
            description: Production
            host: prod.database.windows.net
            protected: true
            allowRevert: false
            ordering: strict
            password: ${env:BADGER_TEST_UNSET_SECRET}
            connectionProperties: { Command Timeout: 300 }
            alwaysEncrypted:
              columnMasterKeys:
                CMK_Prod: { keyStore: AZURE_KEY_VAULT, keyPath: "https://kv/keys/cmk/1" }
              columnEncryptionKeys:
                CEK_Customer: { columnMasterKey: CMK_Prod }
        """;

    [Fact]
    public void Extends_deep_merges_and_children_win()
    {
        var dev = Load(Sample).ResolveEnvironment("dev", null);

        Assert.Equal("dev.database.windows.net", dev.Host);
        Assert.Equal("App", dev.Database);
        Assert.Equal(AuthenticationKind.EntraInteractive, dev.Authentication);
        Assert.Equal(TlsMode.Strict, dev.Tls.Mode);
        Assert.Equal("tenant-1", dev.Entra.TenantId);
        Assert.Equal("dev@contoso.com", dev.Entra.LoginHint);
        Assert.Equal("3", dev.ConnectionProperties["Connect Retry Count"]);
        Assert.Equal("dev_role", dev.Variables["AppRole"]);
        Assert.Equal("calc_{env}", dev.Variables["schemas.calc"]);
        Assert.False(dev.Protected);
        Assert.Empty(dev.ProviderSettings);
    }

    [Fact]
    public void Production_policies_and_provider_settings()
    {
        Environment.SetEnvironmentVariable("BADGER_TEST_UNSET_SECRET", "s3cret");
        try
        {
            var prod = Load(Sample).ResolveEnvironment("prod", null);
            Assert.True(prod.Protected);
            Assert.False(prod.AllowRevert);
            Assert.Equal(OrderingPolicy.Strict, prod.Ordering);
            Assert.Equal("300", prod.ConnectionProperties["Command Timeout"]);
            Assert.Equal("3", prod.ConnectionProperties["Connect Retry Count"]);
            Assert.Equal("s3cret", prod.Password);
            Assert.Equal("https://kv/keys/cmk/1", prod.ProviderSettings["alwaysEncrypted"]!["columnMasterKeys"]!["CMK_Prod"]!["keyPath"]!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BADGER_TEST_UNSET_SECRET", null);
        }
    }

    [Fact]
    public void Secrets_are_resolved_only_for_the_selected_environment()
    {
        var config = Load(Sample);
        _ = config.ResolveEnvironment("dev", null);
        var ex = Assert.Throws<MigrationException>(() => config.ResolveEnvironment("prod", null));
        Assert.Contains("BADGER_TEST_UNSET_SECRET", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Templates_and_defaults()
    {
        var config = Load(Sample);
        Assert.Equal(["dev", "prod"], config.DeployableEnvironments);
        Assert.Equal("dev", config.SelectEnvironmentName(null));
        Assert.Throws<MigrationException>(() => config.SelectEnvironmentName("_azure"));
        Assert.Throws<MigrationException>(() => config.SelectEnvironmentName("staging"));
    }

    [Fact]
    public void Preprocessor_options_and_variable_names()
    {
        var config = Load(Sample);
        Assert.Equal(UnknownVariablePolicy.Keep, config.Preprocessor.Unknown);
        Assert.False(config.Preprocessor.SqlCmdSyntax);
        Assert.Contains("quoted.stage", config.VariableNames("prod"));
        Assert.Equal("stage_{env}", config.ResolveEnvironment("dev", null).Variables["quoted.stage"]);
        Assert.Equal("app_user", config.Variables["AppRole"]);
    }

    [Fact]
    public void Typos_in_known_blocks_are_rejected()
    {
        var config = Load("""
            environments:
              dev:
                host: x
                entra: { tennantId: abc }
            """);
        var ex = Assert.Throws<MigrationException>(() => config.ResolveEnvironment("dev", null));
        Assert.Contains("tennantId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Quoted_scalars_stay_strings_and_plain_ones_are_typed()
    {
        var env = Load("""
            environments:
              dev:
                host: x
                port: 1444
                protected: "true"
            """);
        var ex = Assert.Throws<MigrationException>(() => env.ResolveEnvironment("dev", null));
        Assert.Contains("protected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1444, Load("environments: { dev: { host: x, port: 1444 } }").ResolveEnvironment("dev", null).Port);
    }

    [Fact]
    public void Extends_cycles_and_old_profiles_key_are_reported()
    {
        Assert.Contains("cycle", Assert.Throws<MigrationException>(() => Load("environments: { a: { extends: b }, b: { extends: a } }").ResolveEnvironment("a", null)).Message, StringComparison.Ordinal);
        Assert.Contains("renamed to 'environments'", Assert.Throws<MigrationException>(() => Load("profiles: {}")).Message, StringComparison.Ordinal);
    }
}
