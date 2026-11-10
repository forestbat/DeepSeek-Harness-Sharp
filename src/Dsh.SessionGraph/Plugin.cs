using Dsh.Core;
using Dsh.Interaction;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.SessionGraph.Plugin.SessionGraph)]

namespace Dsh.SessionGraph;

/** @deepseek-ai/dsh-session-graph: 会话事件图与 /timestamp 时间线命令。 */
public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string SessionGraph = "@deepseek-ai/dsh-session-graph";

    public string[] Inject => packageName switch
    {
        SessionGraph => [SessionStore.ServiceName, CommandsService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        SessionGraph => new DisposableBundle(new SessionEventGraphService(ctx), TimestampCommand.Register(ctx)),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private sealed class DisposableBundle(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
                item.Dispose();
        }
    }
}
