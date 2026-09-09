using Cordis;
using Dsh.Boot;
using Dsh.Interaction;

namespace Dsh.Mcp;

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
                var servers = settings.McpServers
                    .Where(entry => entry.Value.Enabled)
                    .Select(entry =>
                    {
                        var commandParts = entry.Value.Command ?? [];
                        var command = commandParts.Count > 0 ? commandParts[0] : null;
                        var args = commandParts.Skip(1).Concat(entry.Value.Args ?? []).ToList();
                        return new McpServerConfig(
                            entry.Key,
                            entry.Value.Transport ?? "stdio",
                            command,
                            args,
                            entry.Value.Url);
                    })
                    .ToList();
                if (servers.Count == 0)
                    return new CommandResult.Success("no mcp servers enabled");
                await using var runtime = new McpRuntime();
                await runtime.ConnectAsync(servers, invocation.Signal);
                var lines = runtime.Status().Select(status =>
                    $"{status.Name}: {status.Transport} {(status.Connected ? $"connected ({status.ToolCount} tools)" : $"error: {status.Error}")}");
                return new CommandResult.Success(string.Join('\n', lines));
            },
        });
    }
}
