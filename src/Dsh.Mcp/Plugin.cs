using Cordis;
using Dsh.Boot;
using Dsh.Interaction;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Mcp.Plugin.Mcp)]

namespace Dsh.Mcp;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Mcp = "@deepseek-ai/dsh-mcp";

    public string[] Inject => packageName switch
    {
        Mcp => [CommandsService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Mcp => RegisterMcpCommand(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterMcpCommand(Context ctx)
    {
        var homePath = ctx.GetProp("dshHomePath") as string
            ?? throw new InvalidOperationException("dshHomePath is required for the mcp plugin");
        return McpCommand.Register(ctx, new HarnessHome(homePath));
    }
}
