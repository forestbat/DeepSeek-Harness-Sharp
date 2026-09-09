using Cordis;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Subagent;

[assembly: DshPlugin(Dsh.Workflow.Plugin.WorkflowWorkerThread)]
[assembly: DshPlugin(Dsh.Workflow.Plugin.ToolWorkflow)]
[assembly: DshPlugin(Dsh.Workflow.Plugin.ToolRalph)]

namespace Dsh.Workflow;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string WorkflowWorkerThread = "@deepseek-ai/dsh-workflow-worker-thread";
    internal const string ToolWorkflow = "@deepseek-ai/dsh-tool-workflow";
    internal const string ToolRalph = "@deepseek-ai/dsh-tool-ralph";

    public string[] Inject => packageName switch
    {
        WorkflowWorkerThread => [SubagentRuntime.ServiceName],
        ToolWorkflow => [ToolRuntime.ServiceName, WorkflowEngine.ServiceName, SystemPrompt.ServiceName],
        ToolRalph => [ToolRuntime.ServiceName, WorkflowEngine.ServiceName, SubagentRuntime.ServiceName, SystemPrompt.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        WorkflowWorkerThread => WorkerThreadWorkflowEngine.Register(ctx, config),
        ToolWorkflow => Dsh.Workflow.ToolWorkflow.Apply(ctx, config),
        ToolRalph => Dsh.Workflow.ToolRalph.Apply(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };
}