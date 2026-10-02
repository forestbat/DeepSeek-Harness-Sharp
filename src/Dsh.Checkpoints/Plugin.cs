using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Checkpoints.Plugin.Checkpoints)]

namespace Dsh.Checkpoints;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Checkpoints = "@deepseek-ai/dsh-checkpoints";

    public string[] Inject => packageName switch
    {
        Checkpoints => [SessionStore.ServiceName, CommandsService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config)
    {
        var homeRoot = ctx.GetProp("dshHomePath") as string ?? HarnessHome.Resolve().Root;
        var service = new CheckpointService(ctx, CheckpointPolicy.Resolve(config), homeRoot);
        return new DisposableBundle(service, CheckpointCommand.Register(ctx, service));
    }

    private sealed class DisposableBundle(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            for (var index = disposables.Length - 1; index >= 0; index--)
                disposables[index].Dispose();
        }
    }
}
