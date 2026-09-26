using Dsh.Runtime;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Board.Plugin.Board)]
[assembly: DshPlugin(Dsh.Board.Plugin.ToolBoard)]

namespace Dsh.Board;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Board = "@deepseek-ai/dsh-board";
    internal const string ToolBoard = "@deepseek-ai/dsh-tool-board";

    public string[] Inject => packageName switch
    {
        Board => [],
        ToolBoard => [ToolRuntime.ServiceName, SwarmBoard.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Board => RegisterBoard(ctx),
        ToolBoard => BoardTools.Apply(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterBoard(Context ctx)
    {
        SwarmBoard.Register(ctx);
        return new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
