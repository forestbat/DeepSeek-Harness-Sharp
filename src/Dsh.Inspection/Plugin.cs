using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Inspection.Plugin.InspectTools)]
[assembly: DshPlugin(Dsh.Inspection.Plugin.PluginManager)]

namespace Dsh.Inspection;

/** Independent runtime inspection and approval-gated plugin management capabilities. */
public sealed class Plugin(string packageName) : IDshPlugin
{
    public const string InspectTools = "@deepseek-ai/dsh-tool-creative";
    public const string PluginManager = "@deepseek-ai/dsh-plugin-manager";

    public string[] Inject => packageName switch
    {
        InspectTools => [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        PluginManager => [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        InspectTools => ToolCreative.Register(ctx),
        PluginManager => ToolPluginManager.Register(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };
}
