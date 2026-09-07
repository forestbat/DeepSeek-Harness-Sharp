using Cordis;
using Dsh.Interaction;
using Dsh.Mcp;

namespace Dsh.Boot;

public static class McpCommand
{
    public static IDisposable Register(Context ctx, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = "mcp",
            Description = "Show MCP server connection status",
            Handler = async invocation =>
            {
                var settings = HarnessSettings.Load(home);
                if (settings.McpServers.Count == 0)
                    return new CommandResult.Success("no mcpServers configured");
                var servers = settings.McpServers
                    .Select(entry => new McpServerConfig(
                        entry.Key,
                        entry.Value.Transport ?? "stdio",
                        entry.Value.Command,
                        entry.Value.Args,
                        entry.Value.Url))
                    .ToList();
                await using var runtime = new McpRuntime();
                await runtime.ConnectAsync(servers, invocation.Signal);
                var lines = runtime.Status().Select(status =>
                    $"{status.Name}: {status.Transport} {(status.Connected ? $"connected ({status.ToolCount} tools)" : $"error: {status.Error}")}");
                return new CommandResult.Success(string.Join('\n', lines));
            },
        });
    }
}