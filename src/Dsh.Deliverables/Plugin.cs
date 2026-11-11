using Dsh.Core;
using Dsh.Interaction;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Agent;

[assembly: DshPlugin(Dsh.Deliverables.Plugin.ToolPresent)]

namespace Dsh.Deliverables;

/** @deepseek-ai/dsh-tool-present: the present tool, its prompt section and the /deliverables command. */
public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string ToolPresent = "@deepseek-ai/dsh-tool-present";

    public string[] Inject => packageName switch
    {
        ToolPresent =>
        [
            ToolRuntime.ServiceName,
            SystemPrompt.ServiceName,
            SessionProjectionRegistry.ServiceName,
            CommandsService.ServiceName,
        ],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        ToolPresent => new Bundle(PresentTool.Register(ctx), DeliverablesCommand.Register(ctx)),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private sealed class Bundle(IDisposable tool, IDisposable command) : IDisposable
    {
        public void Dispose()
        {
            command.Dispose();
            tool.Dispose();
        }
    }
}
