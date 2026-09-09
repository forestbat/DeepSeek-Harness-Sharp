using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Interaction.AskUser.Plugin.ToolAskUser)]

namespace Dsh.Interaction.AskUser;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string ToolAskUser = "@deepseek-ai/dsh-tool-ask-user";

    public string[] Inject => packageName switch
    {
        ToolAskUser => [ToolRuntime.ServiceName, UserQuestionService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        ToolAskUser => AskUserTool.Register(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };
}