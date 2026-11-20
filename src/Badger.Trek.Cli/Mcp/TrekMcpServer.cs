using System.Reflection;
using Badger.Trek.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Badger.Trek.Cli.Mcp;

/// <summary>'trek mcp': a Model Context Protocol server over stdio for one project folder.</summary>
public static class TrekMcpServer
{
    public const string GuideUri = "trek://guide";

    public static McpServerOptions CreateOptions(string projectDir)
    {
        var dir = Path.GetFullPath(projectDir);
        if (!File.Exists(Path.Combine(dir, TrekConfig.FileName)))
        {
            throw new MigrationException($"{Path.Combine(dir, TrekConfig.FileName)} not found. Pass the project folder: trek mcp -C <folder> (create one with 'trek init').");
        }

        var tools = new ProjectTools(dir);
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in typeof(ProjectTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            {
                toolCollection.Add(McpServerTool.Create(method, method.IsStatic ? null : tools));
            }
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "trek", Title = "Badger Trek", Version = Commands.ToolVersion },
            ServerInstructions =
                "Tools for writing database changes in a Badger Trek project. Call project_info first to learn the SQL dialect, " +
                "and read the guide before writing scripts. Add changes with add_change (never edit trek.plan by hand), then run check and fix what it reports. " +
                "These tools never connect to a database: tell the user to run 'trek deploy' themselves.",
            ToolCollection = toolCollection,
            ResourceCollection =
            [
                McpServerResource.Create(() => Commands.AiGuide, new McpServerResourceCreateOptions
                {
                    UriTemplate = GuideUri,
                    Name = "guide",
                    Title = "Writing Badger Trek changes",
                    Description = "Rules and examples for deploy, revert and verify scripts, repeatables, variables and lint codes.",
                    MimeType = "text/markdown",
                }),
            ],
            PromptCollection =
            [
                McpServerPrompt.Create(
                    (string request) => new[]
                    {
                        new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = Commands.AiGuide } },
                        new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text =
                            $"Write a Badger Trek change for this request: {request}\n\n" +
                            "Call project_info for the dialect and plan_show for existing changes, then add_change with deploy, revert and verify scripts " +
                            "(and requires for the changes it builds on). Put views, procedures and functions in repeatables with write_repeatable. " +
                            "Run check and fix every finding. Finish by telling me what to review and to run 'trek deploy'." } },
                    },
                    new McpServerPromptCreateOptions { Name = "write-migration", Title = "Write a migration", Description = "Write a new change for a described schema or data change." }),
            ],
        };
    }

    public static async Task<int> RunAsync(string projectDir, CancellationToken cancellationToken)
    {
        var options = CreateOptions(projectDir);
        await using var transport = new StdioServerTransport(options);
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return Commands.Ok;
    }
}
