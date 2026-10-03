using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Interaction;

namespace Dsh.Mcp;

public static class McpCommand
{
    public static IDisposable Register(Context ctx, McpService service, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = "mcp",
            Description = "Show MCP server connection status; /mcp reload reconnects from settings",
            Handler = async invocation =>
            {
                if (string.Equals(invocation.RawInput.Trim(), "reload", StringComparison.OrdinalIgnoreCase))
                {
                    var servers = McpServerConfig.FromSettings(HarnessSettings.Load(home));
                    await service.ReloadAsync(servers, invocation.Signal);
                }
                return new CommandResult.Success(Format(service.Status()));
            },
        });
    }

    internal static string Format(IReadOnlyList<McpServerStatus> statuses)
        => statuses.Count == 0
            ? "no mcp servers enabled"
            : string.Join('\n', statuses.Select(status => status.Connected
                ? $"{status.Name}: {status.Transport} connected ({status.ToolCount} tools registered)"
                : $"{status.Name}: {status.Transport} error: {status.Error}"));
}
