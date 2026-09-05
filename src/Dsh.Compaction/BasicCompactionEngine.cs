using System.Runtime.CompilerServices;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

public class BasicCompactionEngine : CompactionEngine, IDisposable
{
    private sealed class RetryCount
    {
        public int Value;
    }

    private readonly LlmRuntime _llm;
    private readonly TokenMeter _meter;
    private readonly SessionStore _sessions;
    private readonly List<Func<bool>> _listeners = [];
    private readonly HashSet<string> _warnedPressureConfigTargets = [];
    private readonly ConditionalWeakTable<IAgent, RetryCount> _overflowRetries = new();
    private readonly ConditionalWeakTable<Session, IAgent> _overflowAgents = new();

    public ResolvedConfig Config { get; }

    static BasicCompactionEngine() => CompactionEventTypes.EnsureRegistered();

    public BasicCompactionEngine(Context ctx, BasicCompactionConfig? config = null) : base(ctx)
    {
        _llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)
            ?? throw new InvalidOperationException("compaction requires the llm service");
        _meter = ctx.Get<TokenMeter>(TokenMeter.ServiceName)
            ?? throw new InvalidOperationException("compaction requires the tokenMeter service");
        _sessions = ctx.Get<SessionStore>(SessionStore.ServiceName)
            ?? throw new InvalidOperationException("compaction requires the sessions service");
        Config = CompactionConfigResolver.ResolveConfig(config);
        if (Config.Auto)
            RegisterAutomaticCompaction();
    }

    public static BasicCompactionEngine Register(Context ctx, BasicCompactionConfig? config = null) => new(ctx, config);

    public void Dispose()
    {
        foreach (var listener in _listeners)
            listener();
        _listeners.Clear();
    }

    private (string Provider, string Model)? RoutedTarget(Session session)
    {
        var config = session.RequestHeader()?.Config;
        if (config is null || config.Provider.Length == 0 || config.Model.Length == 0)
            return null;
        return (config.Provider, config.Model);
    }

    private (string Provider, string Model)? ConversationTarget(IAgent agent)
    {
        var routed = RoutedTarget(agent.Session);
        if (routed is not null)
            return routed;
        if (string.IsNullOrEmpty(agent.Options.Provider) || string.IsNullOrEmpty(agent.Options.Model))
            return null;
        return (agent.Options.Provider, agent.Options.Model);
    }

    protected virtual Task<SummaryResult> Summarize(SummarizationInput input, IAgent agent, CancellationToken signal)
    {
        var target = ConversationTarget(agent);
        var policy = target is null
            ? CompactionConfigResolver.ResolveTargetPolicy(Config, "", "")
            : CompactionConfigResolver.ResolveTargetPolicy(Config, target.Value.Provider, target.Value.Model);
        return Summarizer.SummarizeWithLlm(_llm, policy.SummarizationProvider, policy.SummarizationModel, policy.MaxTokens, input, agent, signal);
    }

    public override async Task<CompactionResult?> CompactIfNeeded(IAgent agent, CompactionTrigger trigger, CancellationToken signal)
    {
        var target = RoutedTarget(agent.Session);
        if (target is null)
            return null;
        var policy = CompactionConfigResolver.ResolveTargetPolicy(Config, target.Value.Provider, target.Value.Model);
        var measurement = _meter.Measure(agent.Session);
        var prune = Ctx.Get<ToolResultPruner>(ToolResultPruner.ServiceName, false);

        if (trigger == CompactionTrigger.ContextOverflow)
        {
            if (prune is not null)
            {
                prune.PruneSession(agent.Session);
                measurement = _meter.Measure(agent.Session);
            }
            var overflowRange = CompactionRegion.SelectCompactableRange(agent.Session, measurement, 0);
            if (overflowRange is null)
                return null;
            return await CompactRegion(overflowRange.Start, overflowRange.End, agent, signal);
        }

        var contextWindow = _llm.ResolveModelInfo(target.Value.Provider, target.Value.Model).ContextWindow;
        CompactionRegion.AssertNoActiveCompaction(agent.Session, "automatic pressure compaction");
        var targetKey = $"{target.Value.Provider}/{target.Value.Model}";
        if (contextWindow is null)
            throw new TargetPressureConfigError(
                targetKey,
                $"compaction-basic: no context capacity for {targetKey}; configure contextWindow on that adapter model");
        var spec = CompactionConfigResolver.ResolveCompactSpec(policy, contextWindow.Value);
        if (measurement.TotalTokens < spec.ThresholdTokens)
            return null;

        if (prune is not null)
        {
            prune.PruneSession(agent.Session);
            measurement = _meter.Measure(agent.Session);
        }
        if (measurement.TotalTokens < spec.ThresholdTokens)
            return null;

        CompactionResult? result = null;
        for (var attempt = 0; attempt <= spec.CompactionRetries; attempt++)
        {
            var range = CompactionRegion.SelectCompactableRange(agent.Session, measurement, spec.RetainTokens);
            if (range is null)
            {
                if (result is null)
                    return null;
                break;
            }
            result = await CompactRegion(range.Start, range.End, agent, signal);
            measurement = _meter.Measure(agent.Session);
            if (measurement.TotalTokens < spec.ThresholdTokens)
                return result;
        }

        throw new InvalidOperationException(
            $"compaction still above threshold after {spec.CompactionRetries + 1} compaction attempts "
            + $"({measurement.TotalTokens} estimated tokens >= threshold {spec.ThresholdTokens})");
    }

    public override Task<CompactionResult> CompactRegion(long start, long end, IAgent agent, CancellationToken signal = default)
        => CompactionRegion.CompactSurfaceRegion(
            _meter,
            Summarize,
            agent.Session,
            start,
            end,
            agent,
            new CompactionTransactionOptions(CompactionOwner.CurrentTurn, CompactionStability.WholeSurface),
            signal);

    public override Task<CompactionResult?> CompactNow(IAgent agent, CancellationToken signal, string? sourceCommandId = null)
    {
        signal.ThrowIfCancellationRequested();
        try
        {
            if (agent is not AgentLoopAgent loopAgent)
                throw new InvalidOperationException("agent does not support maintenance operations");
            return AwaitMaintenance(loopAgent.RunMaintenance(Operation));

            async Task<CompactionResult?> Operation(CancellationToken agentSignal)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(agentSignal, signal);
                var operationSignal = linked.Token;
                try
                {
                    operationSignal.ThrowIfCancellationRequested();
                    var range = CompactionRegion.SelectCompactableRange(agent.Session, _meter.Measure(agent.Session), 0);
                    if (range is null)
                        return null;
                    return await CompactionRegion.CompactSurfaceRegion(
                        _meter,
                        Summarize,
                        agent.Session,
                        range.Start,
                        range.End,
                        agent,
                        new CompactionTransactionOptions(
                            CompactionOwner.Standalone,
                            CompactionStability.SelectedSpan,
                            sourceCommandId,
                            () => _sessions.Flush(agent.Session)),
                        operationSignal);
                }
                catch (Exception error)
                {
                    if (agentSignal.IsCancellationRequested && !signal.IsCancellationRequested)
                        throw new ManualCompactionError(ManualCompactionErrorCode.Cancelled, "manual compaction was cancelled", error);
                    operationSignal.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }
        catch (Exception error)
        {
            throw new ManualCompactionError(
                ManualCompactionErrorCode.Busy,
                "manual compaction requires an idle agent with no waking queued work",
                error);
        }
    }

    private static async Task<CompactionResult?> AwaitMaintenance(Task<CompactionResult?> task)
    {
        try
        {
            return await task;
        }
        catch (InvalidOperationException error) when (error.Message.EndsWith("already has active work"))
        {
            // C# 的 RunMaintenance 是 async 方法,TS 中同步抛出的空闲守卫在 C# 落入返回的 Task;此处还原 busy 语义。
            throw new ManualCompactionError(
                ManualCompactionErrorCode.Busy,
                "manual compaction requires an idle agent with no waking queued work",
                error);
        }
    }

    private void RegisterAutomaticCompaction()
    {
        _listeners.Add(Ctx.On(AgentEventNames.PreStep, async (thisArg, args) =>
        {
            var payload = (PreStepPayload)args[0]!;
            var next = (Func<ValueTask<object?>>)args[1]!;
            if (!payload.Signal.IsCancellationRequested)
            {
                try
                {
                    var result = await CompactIfNeeded(payload.Agent, CompactionTrigger.Pressure, payload.Signal);
                    if (result is not null)
                        LogResult(result, "step pressure");
                }
                catch (Exception error)
                {
                    if (error is TargetPressureConfigError configError && !_warnedPressureConfigTargets.Add(configError.TargetKey))
                        return await next();
                    Ctx.LoggerFor(ServiceName).Warn($"step compaction failed: {error.Message}; continuing the turn");
                }
            }
            return await next();
        }, new EventOptions { Global = true }));

        _listeners.Add(Ctx.On(AgentEventNames.Status, (thisArg, args) =>
        {
            var payload = args[0]!;
            if (payload.GetType().GetProperty("Status")?.GetValue(payload) is AgentStatus.Idle
                && payload.GetType().GetProperty("Agent")?.GetValue(payload) is IAgent agent)
                _overflowRetries.Remove(agent);
            return new ValueTask<object?>();
        }, new EventOptions { Global = true }));

        _listeners.Add(Ctx.On(SessionStore.EventEvent, (thisArg, args) =>
        {
            if (args[1] is SessionEvent { Type: SessionEventTypes.AssistantMessage }
                && args[0] is Session session
                && _overflowAgents.TryGetValue(session, out var agent))
                _overflowRetries.Remove(agent);
            return new ValueTask<object?>();
        }, new EventOptions { Global = true }));

        _listeners.Add(Ctx.On(AgentEventNames.RequestError, async (thisArg, args) =>
        {
            var payload = (AgentRequestErrorPayload)args[0]!;
            var next = (Func<ValueTask<object?>>)args[1]!;
            if (payload.Failure.Code != LlmFailureCodes.ContextWindowExceeded || payload.Signal.IsCancellationRequested)
                return await next();
            var agent = payload.Agent;
            _overflowAgents.AddOrUpdate(agent.Session, agent);
            var target = RoutedTarget(agent.Session);
            if (target is null)
                return await next();
            var policy = CompactionConfigResolver.ResolveTargetPolicy(Config, target.Value.Provider, target.Value.Model);
            var retries = _overflowRetries.TryGetValue(agent, out var count) ? count.Value : 0;
            if (retries >= policy.MaxOverflowRetries)
                return await next();

            var generation = agent.Session.SurfaceManager.ReplaceGeneration;
            CompactionResult? result;
            try
            {
                result = await CompactIfNeeded(agent, CompactionTrigger.ContextOverflow, payload.Signal);
            }
            catch (Exception error)
            {
                if (!payload.Signal.IsCancellationRequested && agent.Session.SurfaceManager.ReplaceGeneration > generation)
                {
                    Ctx.LoggerFor(ServiceName).Warn(
                        $"context-overflow compaction failed after durable surface progress: {error.Message}; retrying from the replacement surface");
                    SetOverflowRetries(agent, retries + 1);
                    return new RequestErrorAction.Retry();
                }
                Ctx.LoggerFor(ServiceName).Warn(
                    $"context-overflow compaction failed: {error.Message}; {(payload.Signal.IsCancellationRequested ? "cancellation prevents retry" : "preserving the original request error")}");
                return await next();
            }
            if (payload.Signal.IsCancellationRequested || agent.Session.SurfaceManager.ReplaceGeneration <= generation)
                return await next();
            if (result is not null)
                LogResult(result, "context overflow recovery");
            SetOverflowRetries(agent, retries + 1);
            return new RequestErrorAction.Retry();
        }, new EventOptions { Global = true }));
    }

    private void SetOverflowRetries(IAgent agent, int retries)
        => _overflowRetries.GetValue(agent, _ => new RetryCount()).Value = retries;

    private void LogResult(CompactionResult result, string trigger)
        => Ctx.LoggerFor(ServiceName).Info(
            $"compaction ({trigger}): shadowed {result.ShadowedSeqs.Count} surface nodes "
            + $"(seqs {result.ShadowedRange.Start}-{result.ShadowedRange.End}, "
            + $"~{result.ShadowedTokenCount} tokens)");
}
