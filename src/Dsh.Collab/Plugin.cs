using Dsh.Agent;
using Dsh.Interaction;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Collab.Plugin.Package)]

namespace Dsh.Collab;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Package = "@deepseek-ai/dsh-collab";

    public string[] Inject => packageName switch
    {
        Package => [ToolRuntime.ServiceName, CommandsService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Package => ApplyCollab(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable ApplyCollab(Context ctx, object? config)
    {
        var dict = config as IReadOnlyDictionary<string, object?>;
        var collabDir = dict?.GetValueOrDefault("collabDir") as string;
        var rooms = new CollaborationRoomService(ctx, collabDirectory: collabDir);
        var discovery = new AgentDiscoveryService(ctx, collabDirectory: collabDir);
        var tools = CollabTools.Apply(ctx, rooms, discovery);
        var commands = CollabCommands.Apply(ctx, rooms, discovery);
        return new DisposeBundle([commands, tools, discovery, rooms]);
    }

    private sealed class DisposeBundle(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
