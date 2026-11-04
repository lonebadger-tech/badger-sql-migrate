using System.Text.Json;
using Badger.SqlMigrate.Core.Plan;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Badger.SqlMigrate.Cli.Tests;

/// <summary>Starts 'bsm mcp' as a real stdio server and drives it with the MCP client, as an AI assistant would.</summary>
public sealed class McpServerTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bsm-mcp-tests", Guid.NewGuid().ToString("N"));
    private McpClient client = null!;

    private GlobalOptions G => new(root, null, null, [], false, new ConsoleOutput(true, false, true));

    public async Task InitializeAsync()
    {
        Assert.Equal(0, Commands.Init(G, "postgresql"));
        Commands.AddChange(root, "create-customer", [], null, false, false, "CREATE TABLE public.customer (id int PRIMARY KEY, name text NOT NULL);", "DROP TABLE public.customer;");

        client = await ConnectAsync(protocolVersion: null);
    }

    /// <summary>
    /// Starts 'bsm mcp'. With no protocol version the client probes for the newest revision (server/discover);
    /// the probe gets a generous timeout because starting 'dotnet bsm.dll' can take seconds on a CI runner, and a
    /// probe that times out makes the client fall back to initialize on a revision that no longer has it.
    /// </summary>
    private async Task<McpClient> ConnectAsync(string? protocolVersion)
    {
        var bsm = Path.Combine(AppContext.BaseDirectory, "bsm.dll");
        return await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "bsm",
                Command = "dotnet",
                Arguments = [bsm, "mcp", "-C", root],
                // No BSM_LOCAL_PASSWORD: nothing the server does may need a connection secret.
                EnvironmentVariables = new Dictionary<string, string?> { ["BSM_LOCAL_PASSWORD"] = null },
            }),
            new McpClientOptions { ProtocolVersion = protocolVersion, DiscoverProbeTimeout = TimeSpan.FromSeconds(60), InitializationTimeout = TimeSpan.FromSeconds(60) });
    }

    [Fact]
    public async Task Clients_using_the_initialize_handshake_work_too()
    {
        // Most clients today (Claude Code, Claude Desktop, VS Code, Cursor) start with initialize.
        await using var older = await ConnectAsync("2025-06-18");
        Assert.Equal("bsm", older.ServerInfo?.Name);
        Assert.Contains(await older.ListToolsAsync(), t => t.Name == "add_change");
    }

    public async Task DisposeAsync()
    {
        await client.DisposeAsync();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<(bool Error, string Text)> Call(string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        return (result.IsError == true, Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    [Fact]
    public async Task Lists_the_file_only_tools_guide_and_prompt()
    {
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).Order().ToList();
        Assert.Equal(
            ["add_change", "check", "guide", "lint_sql", "plan_show", "project_info", "read_change", "render", "rework_change", "update_change", "write_change_script", "write_repeatable"],
            tools);

        var guide = await client.ReadResourceAsync("bsm://guide");
        Assert.StartsWith("# Writing Badger SQL Migrate changes", Assert.IsType<TextResourceContents>(guide.Contents[0]).Text);
        var prompt = await client.GetPromptAsync("write-migration", new Dictionary<string, object?> { ["request"] = "an orders table" });
        Assert.Contains("an orders table", Assert.IsType<TextContentBlock>(prompt.Messages[^1].Content).Text);
    }

    [Fact]
    public async Task Add_change_writes_the_same_plan_line_and_files_as_bsm_add()
    {
        var (error, text) = await Call("add_change", new()
        {
            ["name"] = "add-customer-email",
            ["requires"] = new[] { "create-customer" },
            ["deploy"] = "ALTER TABLE public.customer ADD COLUMN email text;\r\n",
            ["revert"] = "ALTER TABLE public.customer DROP COLUMN email;",
        });
        Assert.False(error, text);

        var plan = PlanFile.Load(Path.Combine(root, PlanFile.FileName));
        Assert.Equal(["create-customer"], plan.Changes[^1].Requires);
        Assert.Equal("ALTER TABLE public.customer ADD COLUMN email text;\n", File.ReadAllText(Path.Combine(root, "changes", "add-customer-email", "deploy.sql")));
        Assert.Equal("ALTER TABLE public.customer DROP COLUMN email;\n", File.ReadAllText(Path.Combine(root, "changes", "add-customer-email", "revert.sql")));
        Assert.False(File.Exists(Path.Combine(root, "changes", "add-customer-email", "verify.sql")));

        (error, text) = await Call("add_change", new() { ["name"] = "no-revert", ["deploy"] = "SELECT 1;" });
        Assert.True(error);
        Assert.Contains("revert script is required", text);
    }

    [Fact]
    public async Task Check_reports_lint_codes_and_update_change_fixes_them()
    {
        await Call("add_change", new()
        {
            ["name"] = "customer-name-index",
            ["requires"] = new[] { "create-customer" },
            ["deploy"] = "CREATE INDEX CONCURRENTLY IF NOT EXISTS customer_name_idx ON public.customer (name);",
            ["revert"] = "DROP INDEX CONCURRENTLY IF EXISTS public.customer_name_idx;",
        });

        var (_, text) = await Call("check", []);
        var report = JsonDocument.Parse(text).RootElement;
        Assert.False(report.GetProperty("ok").GetBoolean());
        var finding = report.GetProperty("findings")[0];
        Assert.Equal("BSM001", finding.GetProperty("code").GetString());
        Assert.Equal("changes/customer-name-index/deploy.sql", finding.GetProperty("path").GetString());

        var (error, updated) = await Call("update_change", new() { ["name"] = "customer-name-index", ["notx"] = true });
        Assert.False(error, updated);
        Assert.Contains("!notx", updated);
        (_, text) = await Call("check", []);
        Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("ok").GetBoolean(), text);
        Assert.Contains("customer-name-index [create-customer] !notx", File.ReadAllText(Path.Combine(root, PlanFile.FileName)));
    }

    [Fact]
    public async Task Lint_sql_checks_a_snippet_without_writing_it()
    {
        var (_, text) = await Call("lint_sql", new() { ["sql"] = "VACUUM public.customer;" });
        Assert.Equal("BSM001", JsonDocument.Parse(text).RootElement[0].GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(root, "changes", "snippet")));
    }

    [Theory]
    [InlineData("../../evil.sql")]
    [InlineData("views/../../../evil.sql")]
    [InlineData("@dev/../../evil.sql")]
    [InlineData("views/not-sql.txt")]
    public async Task Write_repeatable_stays_inside_the_repeatable_folders(string path)
    {
        var (error, _) = await Call("write_repeatable", new() { ["path"] = path, ["sql"] = "SELECT 1;" });
        Assert.True(error);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(root, "evil.sql"))));
    }

    [Fact]
    public async Task Write_repeatable_and_environment_scripts()
    {
        var (error, text) = await Call("write_repeatable", new() { ["path"] = "views/active_customer.sql", ["sql"] = "CREATE OR REPLACE VIEW public.active_customer AS SELECT id, name FROM public.customer;" });
        Assert.False(error, text);
        Assert.True(File.Exists(Path.Combine(root, "repeatable", "views", "active_customer.sql")));
        (error, text) = await Call("write_repeatable", new() { ["path"] = "@local/seed.sql", ["sql"] = "INSERT INTO public.customer VALUES (1, 'a') ON CONFLICT DO NOTHING;" });
        Assert.False(error, text);
        Assert.True(File.Exists(Path.Combine(root, "environments", "local", "seed.sql")));
    }

    [Fact]
    public async Task Render_and_read_need_no_connection_secrets()
    {
        var (error, text) = await Call("render", new() { ["name"] = "create-customer", ["kind"] = "revert" });
        Assert.False(error, text);
        Assert.Equal("DROP TABLE public.customer;\n", JsonDocument.Parse(text).RootElement.GetProperty("text").GetString());

        (error, text) = await Call("read_change", new() { ["name"] = "create-customer" });
        Assert.False(error, text);
        var change = JsonDocument.Parse(text).RootElement;
        Assert.Equal("DROP TABLE public.customer;\n", change.GetProperty("revert").GetString());
        Assert.False(change.TryGetProperty("verify", out var verify) && verify.ValueKind != JsonValueKind.Null);

        (error, text) = await Call("project_info", []);
        Assert.False(error, text);
        Assert.Equal("postgresql", JsonDocument.Parse(text).RootElement.GetProperty("provider").GetString());
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rework_and_edits_to_unknown_changes()
    {
        var (error, text) = await Call("rework_change", new() { ["name"] = "create-customer" });
        Assert.False(error, text);
        Assert.Contains("create-customer-v2", text);

        (error, text) = await Call("write_change_script", new() { ["name"] = "missing", ["kind"] = "deploy", ["sql"] = "SELECT 1;" });
        Assert.True(error);
        Assert.Contains("not in the plan", text);
        (error, _) = await Call("write_change_script", new() { ["name"] = "create-customer", ["kind"] = "../x", ["sql"] = "SELECT 1;" });
        Assert.True(error);
    }
}

public sealed class AiFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bsm-ai-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private GlobalOptions G => new(root, null, null, [], false, new ConsoleOutput(true, false, true));

    [Fact]
    public void Init_writes_agents_file_for_the_provider_and_keeps_an_existing_one()
    {
        Assert.Equal(0, Commands.Init(G, "sqlserver"));
        var agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        Assert.Contains("**SQL Server", agents);
        Assert.Contains("`CREATE OR ALTER`", agents);
        Assert.DoesNotContain("{{", agents);

        Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "mine");
        Assert.Equal(0, Commands.Init(G, "postgresql"));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(root, "AGENTS.md")));
    }

    [Fact]
    public void No_agents_file_opts_out()
    {
        Assert.Equal(0, Commands.Init(G with { AgentsFile = false }, "sqlserver"));
        Assert.False(File.Exists(Path.Combine(root, "AGENTS.md")));
    }

    [Fact]
    public void Guide_is_embedded_and_names_every_tool()
    {
        var guide = Commands.AiGuide;
        foreach (var tool in new[] { "project_info", "add_change", "update_change", "check", "lint_sql", "render" })
        {
            Assert.Contains($"`{tool}`", guide);
        }

        Assert.DoesNotContain("\r", guide);
    }

    [Fact]
    public void Update_change_keeps_timestamp_and_author_and_checks_dependencies()
    {
        Assert.Equal(0, Commands.Init(G, "sqlserver"));
        Commands.AddChange(root, "a", [], "first", false, false);
        Commands.AddChange(root, "b", [], null, false, false);
        var before = PlanFile.Load(Path.Combine(root, PlanFile.FileName)).Changes[1];

        Commands.UpdateChange(root, "b", noTx: true, irreversible: null, requires: ["a"], note: "second");
        var after = PlanFile.Load(Path.Combine(root, PlanFile.FileName)).Changes[1];
        Assert.Equal(before.PlannedAt, after.PlannedAt);
        Assert.Equal(before.Author, after.Author);
        Assert.Equal(["a"], after.Requires);
        Assert.Equal("second", after.Note);
        Assert.True(after.Flags.HasFlag(ChangeFlags.NoTransaction));
        Assert.Equal("first", PlanFile.Load(Path.Combine(root, PlanFile.FileName)).Changes[0].Note);

        Assert.Throws<Core.MigrationException>(() => Commands.UpdateChange(root, "a", null, null, ["b"], null));
    }
}
