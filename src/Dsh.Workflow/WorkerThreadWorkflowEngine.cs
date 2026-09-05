using System.Text.RegularExpressions;
using Cordis;
using Dsh.Core;
using Dsh.Subagent;
using Jint;

namespace Dsh.Workflow;

public sealed class WorkerThreadWorkflowEngine : WorkflowEngine
{
    private static readonly Regex MetaStatement = new(
        @"^\s*export\s+const\s+meta\b",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly string _provider;
    private readonly int _maxConcurrentAgents;
    private readonly int _maxTotalAgents;
    private readonly int _maxItemsPerCall;
    private readonly int _syncTimeoutMs;
    private readonly int _disposeGraceMs;

    public WorkerThreadWorkflowEngine(Context ctx, object? config) : base(ctx)
    {
        var dict = config as IReadOnlyDictionary<string, object?>;
        _provider = dict?.GetValueOrDefault("provider") as string ?? "spawn";
        _maxConcurrentAgents = IntOf(dict, "maxConcurrentAgents") ?? 0;
        _maxTotalAgents = IntOf(dict, "maxTotalAgents") ?? 1000;
        _maxItemsPerCall = IntOf(dict, "maxItemsPerCall") ?? 4096;
        _syncTimeoutMs = IntOf(dict, "syncTimeoutMs") ?? 5000;
        _disposeGraceMs = IntOf(dict, "disposeGraceMs") ?? 5000;
    }

    public static IDisposable Register(Context ctx, object? config)
    {
        _ = new WorkerThreadWorkflowEngine(ctx, config);
        return new DisposeAction();
    }

    public override IWorkflowRun Start(WorkflowStartRequest request)
    {
        var meta = WorkflowMetaValidator.ValidateMeta(request.Meta);
        AssertBodyParses(request.Script, meta.Name);
        var subagentProvider = ResolveSubagentProvider(request.SubagentProvider);
        var maxTotalAgents = ResolveMaxTotalAgents(request.MaxTotalAgents);
        var id = WorkflowRunId.Create(Guid.NewGuid().ToString());
        var info = new WorkflowRunInfo(id, meta);
        var limits = new WorkerLimits(
            _maxConcurrentAgents == 0
                ? Math.Min(16, Math.Max(1, Environment.ProcessorCount - 2))
                : _maxConcurrentAgents,
            maxTotalAgents,
            _maxItemsPerCall,
            _syncTimeoutMs);
        var subagents = Ctx.Get<SubagentRuntime>(SubagentRuntime.ServiceName)
            ?? throw new InvalidOperationException("workflow engine requires the subagents service");
        var controller = new CancellationTokenSource();
        var port = new WorkflowRunHost.SubagentChildPort(subagents, subagentProvider, request.Parent, controller);
        var observer = new WorkflowExecutionObserver(
            Phase: title => EmitWorkflowEvent("workflow/phase", info, title),
            Log: message => EmitWorkflowEvent("workflow/log", info, message),
            AgentStart: agent => EmitWorkflowEvent("workflow/agent-start", info, agent),
            AgentEnd: agent => EmitWorkflowEvent("workflow/agent-end", info, agent));
        var execution = new WorkflowExecution(
            meta,
            request.Script,
            request.Args,
            limits,
            observer,
            port);
        var run = new WorkflowRunHost(id, meta, execution, _disposeGraceMs, controller);
        EmitWorkflowEvent("workflow/start", info);
        _ = run.Result.ContinueWith(task =>
        {
            var settled = task.Result;
            EmitWorkflowEvent("workflow/end", info, new WorkflowResultInfo(
                settled.StopReason,
                settled.Error,
                settled.AgentsStarted));
        }, TaskContinuationOptions.ExecuteSynchronously);
        return run;
    }

    private string ResolveSubagentProvider(string? overrideProvider)
    {
        var provider = overrideProvider ?? _provider;
        if (provider.Length == 0 || provider != provider.Trim())
            throw new WorkflowError(
                "workflow subagentProvider must be a non-empty normalized string",
                WorkflowErrorCodes.InvalidArgument);
        var subagents = Ctx.Get<SubagentRuntime>(SubagentRuntime.ServiceName)!;
        if (subagents.GetProvider(provider) is null)
            throw new WorkflowError($"no subagent provider registered for \"{provider}\"", WorkflowErrorCodes.AgentStart);
        return provider;
    }

    private int ResolveMaxTotalAgents(int? requested)
    {
        if (requested is null)
            return _maxTotalAgents;
        if (requested < 1)
            throw new WorkflowError("workflow maxTotalAgents must be a positive safe integer", WorkflowErrorCodes.InvalidArgument);
        if (requested > _maxTotalAgents)
        {
            throw new WorkflowError(
                $"workflow maxTotalAgents {requested} exceeds the engine ceiling {_maxTotalAgents}",
                WorkflowErrorCodes.InvalidArgument);
        }

        return requested.Value;
    }

    private static void AssertBodyParses(string body, string name)
    {
        if (MetaStatement.IsMatch(body))
        {
            throw new WorkflowError(
                "workflow meta rides the `meta` request field, not the script: remove the `export const meta = {...}` statement from the body",
                WorkflowErrorCodes.ScriptParse);
        }

        try
        {
            _ = Engine.PrepareScript($" (async () => {{\n{body}\n}})() ");
        }
        catch (Exception error)
        {
            throw new WorkflowError($"workflow script does not parse: {WorkflowRealm.RenderThrown(error)}", WorkflowErrorCodes.ScriptParse, error);
        }
    }

    private static int? IntOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => checked((int)value),
            int value => value,
            _ => null,
        };

    private sealed class DisposeAction : IDisposable
    {
        public void Dispose()
        {
        }
    }
}