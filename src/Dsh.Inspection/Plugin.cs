using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Inspection.Plugin.InspectTools)]
[assembly: DshPlugin(Dsh.Inspection.Plugin.PluginManager)]

namespace Dsh.Inspection;

/** 创造模式运行时能力:@deepseek-ai/dsh-tool-cordis 暴露两个只读检查工具,
 *  @deepseek-ai/dsh-plugin-manager 暴露 plugin_manager 写工具(每个操作逐次审批)。 */
public sealed class Plugin(string packageName) : IDshPlugin
{
    public const string InspectTools = "@deepseek-ai/dsh-tool-cordis";
    public const string PluginManager = "@deepseek-ai/dsh-plugin-manager";

    public string[] Inject => packageName switch
    {
        InspectTools => [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        PluginManager => [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        InspectTools => CreativeToolset.Register(ctx),
        PluginManager => CreativeToolset.Register(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };
}
