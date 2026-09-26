using Dsh.Runtime;
using Dsh.Core;
using Dsh.Ptc;

namespace Dsh.Workflow;

public sealed class WorkerThreadWorkflowEngine : WorkflowEngine
{
    private readonly string _provider;
    private readonly int _maxConcurrentAgents;
    private readonly int _maxTotalAgents;
    private readonly int _maxItemsPerCall;
    private readonly int _runTimeoutMs;
    private readonly int _disposeGraceMs;

    public WorkerThreadWorkflowEngine(Context ctx, object? config) : base(ctx)
    {
        var dict = config as IReadOnlyDictionary<string, object?>;
        _provider = dict?.GetValueOrDefault("provider") as string ?? "spawn";
        _maxConcurrentAgents = IntOf(dict, "maxConcurrentAgents") ?? 0;
        _maxTotalAgents = IntOf(dict, "maxTotalAgents") ?? 1000;
        _maxItemsPerCall = IntOf(dict, "maxItemsPerCall") ?? 4096;
        _runTimeoutMs = IntOf(dict, "runTimeoutMs") ?? 0;
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
        var program = WorkflowProgram.Build(request.Script, request.Args, _maxItemsPerCall);
        AssertProgramCompiles(program);
        var subagentProvider = ResolveSubagentProvider(request.SubagentProvider);
        var maxTotalAgents = ResolveMaxTotalAgents(request.MaxTotalAgents);
        var subprocess = Ctx.Get<SubprocessService>(SubprocessService.ServiceName)
            ?? throw new InvalidOperationException("workflow engine requires the subprocess service");
        var id = WorkflowRunId.Create(Guid.NewGuid().ToString());
        var info = new WorkflowRunInfo(id, meta);
        var limits = new WorkerLimits(
            _maxConcurrentAgents == 0
                ? Math.Min(16, Math.Max(1, Environment.ProcessorCount - 2))
                : _maxConcurrentAgents,
            maxTotalAgents,
            _maxItemsPerCall,
            _runTimeoutMs);
        var subagents = Ctx.Get<ISubagentService>(ISubagentService.ServiceName)
            ?? throw new InvalidOperationException("workflow engine requires the subagents service");
        var controller = new CancellationTokenSource();
        var port = new WorkflowRunHost.SubagentChildPort(subagents, subagentProvider, request.Parent, controller);
        var observer = new WorkflowExecutionObserver(
            Phase: title => Ctx.Emit(new WorkflowPhaseNotification(info, title)),
            Log: message => Ctx.Emit(new WorkflowLogNotification(info, message)),
            AgentStart: agent => Ctx.Emit(new WorkflowAgentStartNotification(info, agent)),
            AgentEnd: agent => Ctx.Emit(new WorkflowAgentEndNotification(info, agent)));
        var execution = new WorkflowExecution(
            program,
            limits,
            observer,
            port,
            subprocess,
            Environment.CurrentDirectory);
        var run = new WorkflowRunHost(id, meta, execution, _disposeGraceMs, controller);
        return run;
    }

    private string ResolveSubagentProvider(string? overrideProvider)
    {
        var provider = overrideProvider ?? _provider;
        if (provider.Length == 0 || provider != provider.Trim())
            throw new WorkflowError(
                "workflow subagentProvider must be a non-empty normalized string",
                WorkflowErrorCodes.InvalidArgument);
        var subagents = Ctx.Get<ISubagentService>(ISubagentService.ServiceName)!;
        if (subagents.GetProviderInfo(provider) is null)
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

    private static void AssertProgramCompiles(string program)
    {
        var compilation = PtcProgramCompiler.Compile(program, WorkflowProgram.Bindings);
        if (compilation.Assembly is null)
        {
            throw new WorkflowError(
                $"workflow script does not compile: {string.Join("; ", compilation.Errors)}",
                WorkflowErrorCodes.ScriptParse);
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
