using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.AgentInstructions.Plugin.AgentInstructions)]

namespace Dsh.AgentInstructions;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string AgentInstructions = "@deepseek-ai/dsh-agent-instructions";

    public string[] Inject => packageName switch
    {
        AgentInstructions => [SystemPrompt.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        AgentInstructions => RegisterAgentInstructions(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterAgentInstructions(Context ctx)
    {
        var options = ctx.GetProp("harnessOptions") as HarnessOptions
            ?? throw new InvalidOperationException("harnessOptions is required for the agent-instructions plugin");
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        return systemPrompt.Section(new PromptSection(
            "agent-instructions",
            _ => AgentInstructionsText.Render(options.Home, options.Cwd)));
    }
}
