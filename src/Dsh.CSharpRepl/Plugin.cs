using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-csharprepl")]

namespace Dsh.CSharpRepl;

/** Registers a normal tool without requiring the optional frontend at startup. */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [ToolRuntime.ServiceName, SubprocessService.ServiceName];

    public Type ConfigType => typeof(CSharpReplConfig);

    public IDisposable Apply(Context ctx, object? config)
        => ToolRunCSharpRepl.Register(ctx, config as CSharpReplConfig ?? new CSharpReplConfig());
}
