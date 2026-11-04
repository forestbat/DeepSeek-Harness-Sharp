using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-csx")]

namespace Dsh.Csx;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [ToolRuntime.ServiceName, SubprocessService.ServiceName];

    public Type ConfigType => typeof(CsxPluginConfig);

    public IDisposable Apply(Context ctx, object? config)
        => ToolRunCsx.Register(ctx, Environment.CurrentDirectory, config as CsxPluginConfig);
}
