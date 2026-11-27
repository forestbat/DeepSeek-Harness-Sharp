using Dsh.Agent;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-ma2a")]

namespace Dsh.Ma2a.Tools;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [ToolRuntime.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
        => new Ma2aTools().Register(ctx);
}
