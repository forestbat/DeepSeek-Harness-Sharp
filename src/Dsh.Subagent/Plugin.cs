using Cordis;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Subagent.Plugin.Subagent)]
[assembly: DshPlugin(Dsh.Subagent.Plugin.SubagentSpawnInProcess)]
[assembly: DshPlugin(Dsh.Subagent.Plugin.SubagentForkInProcess)]
[assembly: DshPlugin(Dsh.Subagent.Plugin.ToolSubagent)]
[assembly: DshPlugin(Dsh.Subagent.Plugin.ToolSubagentControl)]
[assembly: DshPlugin(Dsh.Subagent.Plugin.ToolSubagentControlListAgents)]

namespace Dsh.Subagent;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Subagent = "@deepseek-ai/dsh-subagent";
    internal const string SubagentSpawnInProcess = "@deepseek-ai/dsh-subagent-spawn-in-process";
    internal const string SubagentForkInProcess = "@deepseek-ai/dsh-subagent-fork-in-process";
    internal const string ToolSubagent = "@deepseek-ai/dsh-tool-subagent";
    internal const string ToolSubagentControl = "@deepseek-ai/dsh-tool-subagent-control";
    internal const string ToolSubagentControlListAgents = "@deepseek-ai/dsh-tool-subagent-control/list-agents";

    public string[] Inject => packageName switch
    {
        Subagent => [SystemPrompt.ServiceName],
        SubagentSpawnInProcess => [SubagentRuntime.ServiceName],
        SubagentForkInProcess => [SubagentRuntime.ServiceName],
        ToolSubagent => [ToolRuntime.ServiceName, SubagentRuntime.ServiceName, LlmRuntime.ServiceName],
        ToolSubagentControl => [ToolRuntime.ServiceName],
        ToolSubagentControlListAgents => [ToolRuntime.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Subagent => RegisterSubagentRuntime(ctx),
        SubagentSpawnInProcess => SubagentInProcessProviders.RegisterSpawn(ctx, ProviderName(config)),
        SubagentForkInProcess => SubagentInProcessProviders.RegisterFork(ctx, ProviderName(config)),
        ToolSubagent => SubagentTool.Apply(ctx, config),
        ToolSubagentControl => SubagentControlTools.Apply(ctx),
        ToolSubagentControlListAgents => SubagentControlTools.ApplyListAgents(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterSubagentRuntime(Context ctx)
    {
        SubagentRuntime.Register(ctx);
        return new NoopDisposable();
    }

    private static string? ProviderName(object? config)
        => (config as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault("providerName") as string;

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}