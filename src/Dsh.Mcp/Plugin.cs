using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Mcp.Plugin.Mcp)]

namespace Dsh.Mcp;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Mcp = "@deepseek-ai/dsh-mcp";
    private const string LoggerName = "mcp";

    public string[] Inject => packageName switch
    {
        Mcp => [CommandsService.ServiceName, ToolRuntime.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Mcp => ApplyMcp(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable ApplyMcp(Context ctx)
    {
        var homePath = ctx.GetProp("dshHomePath") as string
            ?? throw new InvalidOperationException("dshHomePath is required for the mcp plugin");
        var home = new HarnessHome(homePath);
        var service = new McpService(ctx);
        var command = McpCommand.Register(ctx, service, home);
        var servers = LoadServers(ctx, home);
        if (servers.Count > 0)
            _ = ConnectBackgroundAsync(ctx, service, servers);
        return new CallbackDisposable(() =>
        {
            command.Dispose();
            _ = DisposeBackgroundAsync(ctx, service);
        });
    }

    internal static List<McpServerConfig> LoadServers(Context ctx, HarnessHome home)
    {
        try
        {
            return McpServerConfig.FromSettings(HarnessSettings.Load(home));
        }
        catch (Exception error)
        {
            ctx.LoggerFor(LoggerName).Warn($"failed to read mcp settings: {error.Message}");
            return [];
        }
    }

    private static async Task ConnectBackgroundAsync(Context ctx, McpService service, List<McpServerConfig> servers)
    {
        try
        {
            await service.ConnectAsync(servers);
        }
        catch (Exception error)
        {
            ctx.LoggerFor(LoggerName).Warn($"mcp connect failed: {error.Message}");
        }
    }

    private static async Task DisposeBackgroundAsync(Context ctx, McpService service)
    {
        try
        {
            await service.DisposeAsync();
        }
        catch (Exception error)
        {
            ctx.LoggerFor(LoggerName).Warn($"mcp dispose failed: {error.Message}");
        }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
