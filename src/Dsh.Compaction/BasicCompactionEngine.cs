using System.Diagnostics;
using System.Runtime.CompilerServices;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

public class BasicCompactionEngine : CompactionEngine, IDisposable
{
    private sealed class RetryCount
    {
        public int Value;
    }

    /**
     * 上一次压力压缩失败时的表面规模: 表面没长回去就不值得再花几分钟重跑一次注定失败的摘要。
     * 手动压缩是用户明确的"再试一次", 会清掉这条记录, 否则它会一直挡在前面。
     */
    private sealed class FailedPressure
    {
        public double Tokens;
    }

    private readonly LlmRuntime _llm;
    private readonly TokenMeter _meter;
    private readonly SessionStore _sessions;
    private readonly List<Func<bool>> _listeners = [];
    private readonly HashSet<string> _warnedPressureConfigTargets = [];
    private readonly ConditionalWeakTable<IAgent, RetryCount> _overflowRetries = new();
    private readonly ConditionalWeakTable<Session, IAgent> _overflowAgents = new();
    private readonly ConditionalWeakTable<Session, FailedPressure> _failedPressure = new();

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

    protected virtual async Task<SummaryResult> Summarize(SummarizationInput input, IAgent agent, CancellationToken signal)
    {
        var target = ConversationTarget(agent);
        var policy = target is null
            ? CompactionConfigResolver.ResolveTargetPolicy(Config, "", "")
            : CompactionConfigResolver.ResolveTargetPolicy(Config, target.Value.Provider, target.Value.Model);
        var result = await CompactionSummarizer.Summarize(
            _llm, policy.SummarizationProvider, policy.SummarizationModel, policy.MaxTokens, input, agent, signal);
        var (summary, title) = SessionTitleExtraction.Extract(result.Summary);
        if (title is null)
            return result;
        if (string.IsNullOrWhiteSpace(agent.Session.Header.Title))
        {
            Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, strict: false)?.Rename(agent.Session.Id, title);
            agent.Session.Rename(title);
        }
        return result with { Summary = summary };
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
            var overflowRange = CompactionRegion.SelectCompactableRange(agent.Session, measurement, 0, policy.TailTurns);
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
                $"compaction-basic: no context window for {targetKey}; automatic compaction takes the model's models.dev limit.context, which this model does not declare");
        var spec = CompactionConfigResolver.ResolveCompactSpec(policy, contextWindow.Value);
        if (measurement.TotalTokens < spec.ThresholdTokens)
        {
            _failedPressure.Remove(agent.Session);
            return null;
        }

        if (prune is not null)
        {
            prune.PruneSession(agent.Session);
            measurement = _meter.Measure(agent.Session);
        }
        if (measurement.TotalTokens < spec.ThresholdTokens)
        {
            _failedPressure.Remove(agent.Session);
            return null;
        }

        /*
         * 压力压缩挂在每一步之前, 而一次失败的摘要要跑几分钟(分块摘要)。表面规模与上次失败时一样,
         * 说明那次失败之后既没有新内容、也没有被别的路径压缩过: 再跑一遍只会再白等几分钟,
         * 界面上的表现就是"发了消息什么都不出来"。所以按表面规模去重, 有新内容(更大)才重试。
         */
        if (_failedPressure.TryGetValue(agent.Session, out var failed) && measurement.TotalTokens <= failed.Tokens)
            return null;

        var failedAt = measurement.TotalTokens;
        CompactionResult? result = null;
        try
        {
            for (var attempt = 0; attempt <= spec.CompactionRetries; attempt++)
            {
                var range = CompactionRegion.SelectCompactableRange(agent.Session, measurement, spec.RetainTokens, spec.TailTurns);
                if (range is null)
                {
                    if (result is null)
                        return null;
                    break;
                }
                result = await CompactRegion(range.Start, range.End, agent, signal);
                measurement = _meter.Measure(agent.Session);
                if (measurement.TotalTokens < spec.ThresholdTokens)
                {
                    _failedPressure.Remove(agent.Session);
                    return result;
                }
            }
        }
        catch
        {
            _failedPressure.AddOrUpdate(agent.Session, new FailedPressure { Tokens = failedAt });
            throw;
        }

        _failedPressure.AddOrUpdate(agent.Session, new FailedPressure { Tokens = failedAt });
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
            new CompactionTransactionOptions(
                CompactionOwner.CurrentTurn,
                CompactionStability.WholeSurface,
                DegradedRetainTurns: ResolvePolicy(agent).DegradedRetainTurns),
            signal);

    /** LLM 工具发起的当场压缩:与自动压缩共用 tail_turns+预算选区,在开启的 turn 内提交并接受可选 focus。 */
    public override async Task<CompactionResult?> CompactInTurn(IAgent agent, string? focus, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        ReleaseInheritedMarker(agent.Session, "in-turn");
        var policy = ResolvePolicy(agent);
        var (retainTokens, tailTurns) = ResolveRetention(agent, policy);
        var measurement = _meter.Measure(agent.Session);
        var range = CompactionRegion.SelectCompactableRange(agent.Session, measurement, retainTokens, tailTurns);
        if (range is null)
            return null;
        LogStart(agent, policy, range, "in-turn");
        var elapsed = Stopwatch.StartNew();
        var result = await CompactionRegion.CompactSurfaceRegion(
            _meter,
            Summarize,
            agent.Session,
            range.Start,
            range.End,
            agent,
            new CompactionTransactionOptions(
                CompactionOwner.CurrentTurn,
                CompactionStability.WholeSurface,
                Focus: focus,
                DegradedRetainTurns: policy.DegradedRetainTurns),
            signal);
        LogResult(result, "in-turn", elapsed.Elapsed);
        return result;
    }

    private ResolvedTargetPolicy ResolvePolicy(IAgent agent)
    {
        var target = ConversationTarget(agent);
        return target is null
            ? CompactionConfigResolver.ResolveTargetPolicy(Config, "", "")
            : CompactionConfigResolver.ResolveTargetPolicy(Config, target.Value.Provider, target.Value.Model);
    }

    private (int RetainTokens, int TailTurns) ResolveRetention(IAgent agent, ResolvedTargetPolicy policy)
    {
        var target = ConversationTarget(agent);
        var contextWindow = target is null
            ? null
            : _llm.ResolveModelInfo(target.Value.Provider, target.Value.Model).ContextWindow;
        return (CompactionConfigResolver.ResolveRetainTokens(policy.Retention, contextWindow), policy.TailTurns);
    }

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
                    ReleaseInheritedMarker(agent.Session, "manual");
                    _failedPressure.Remove(agent.Session);
                    var policy = ResolvePolicy(agent);
                    // 手动压缩先做一次工具结果剪枝: 自动路径一直这么做, 手动路径原先漏了,
                    // 而用户"压不动"时最常按的正是手动压缩, 剪枝能直接省掉最占地方的超长输出。
                    var prune = Ctx.Get<ToolResultPruner>(ToolResultPruner.ServiceName, false);
                    if (prune is not null)
                        LogPrune(prune.PruneSession(agent.Session), "manual");
                    var (retainTokens, tailTurns) = ResolveRetention(agent, policy);
                    var range = CompactionRegion.SelectCompactableRange(
                        agent.Session, _meter.Measure(agent.Session), retainTokens, tailTurns);
                    if (range is null)
                        return null;
                    LogStart(agent, policy, range, "manual");
                    var elapsed = Stopwatch.StartNew();
                    var result = await CompactionRegion.CompactSurfaceRegion(
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
                            () => _sessions.Flush(agent.Session),
                            DegradedRetainTurns: policy.DegradedRetainTurns),
                        operationSignal);
                    LogResult(result, "manual", elapsed.Elapsed);
                    return result;
                }
                catch (Exception error)
                {
                    if (agentSignal.IsCancellationRequested && !signal.IsCancellationRequested)
                        throw new ManualCompactionError(ManualCompactionErrorCode.Cancelled, "manual compaction was cancelled", error);
                    operationSignal.ThrowIfCancellationRequested();
                    // 手动压缩的失败原先只以 CommandResult 的形式回到界面, 日志里什么都不留,
                    // 事后无法判断是选区问题、摘要问题还是持久化问题。
                    Ctx.LoggerFor(ServiceName).Warn($"compaction (manual) failed: {error.GetType().Name}: {error.Message}");
                    throw;
                }
            }
        }
        catch (InvalidOperationException error) when (error.Message.EndsWith("already has active work"))
        {
            // 同步路径上同样可能撞到空闲守卫(例如 RunMaintenance 在调用点之前就被判定失败)。
            throw new ManualCompactionError(
                ManualCompactionErrorCode.BusyTurn,
                "manual compaction requires an idle agent; the agent is still working on a turn",
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
            // 这是"回合还在跑", 不是"另一个压缩占着锁": 两者对用户的含义完全不同(等回合结束 vs 等压缩结束)。
            throw new ManualCompactionError(
                ManualCompactionErrorCode.BusyTurn,
                "manual compaction requires an idle agent; the agent is still working on a turn",
                error);
        }
    }

    private void RegisterAutomaticCompaction()
    {
        _listeners.Add(Ctx.OnWaterfall<AgentPreStepNotification>(async (notification, next) =>
        {
            var payload = notification.Payload;
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

        _listeners.Add(Ctx.On<AgentStatusNotification>(notification =>
        {
            if (notification.Status is AgentStatus.Idle)
                _overflowRetries.Remove(notification.Agent);
        }, new EventOptions { Global = true }));

        _listeners.Add(Ctx.On<SessionEventNotification>(notification =>
        {
            if (notification.Event is { Type: SessionEventTypes.AssistantMessage }
                && _overflowAgents.TryGetValue(notification.Session, out var agent))
                _overflowRetries.Remove(agent);
        }, new EventOptions { Global = true }));

        _listeners.Add(Ctx.OnWaterfall<AgentRequestErrorNotification>(async (notification, next) =>
        {
            var payload = notification.Payload;
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

    private void LogResult(CompactionResult result, string trigger, TimeSpan elapsed)
        => Ctx.LoggerFor(ServiceName).Info(
            $"compaction ({trigger}): shadowed {result.ShadowedSeqs.Count} surface nodes "
            + $"(seqs {result.ShadowedRange.Start}-{result.ShadowedRange.End}, "
            + $"~{result.ShadowedTokenCount} tokens) in {Describe(elapsed)}");

    private void LogResult(CompactionResult result, string trigger) => LogResult(result, trigger, TimeSpan.Zero);

    /**
     * 压缩开始时先落一条日志: 事后要判断"为什么这次压了 16 分钟", 只知道结束时间是没办法的,
     * 必须能看到选区大小、被遮蔽的 token 数, 以及真正被请求的摘要模型。
     */
    private void LogStart(IAgent agent, ResolvedTargetPolicy policy, ShadowedRange range, string trigger)
    {
        var covered = _meter.Measure(agent.Session).Nodes
            .Where(node => node.Seq >= range.Start && node.Seq <= range.End)
            .ToList();
        var tokens = covered.Sum(node => node.Tokens);
        Ctx.LoggerFor(ServiceName).Info(
            $"compaction ({trigger}) started: {covered.Count} surface nodes (seqs {range.Start}-{range.End}, ~{tokens} tokens) "
            + $"-> summary {policy.SummarizationProvider}/{policy.SummarizationModel} (maxTokens {policy.MaxTokens})");
    }

    /** 上一个进程遗留的压缩标记会让会话永远压不动; 这里关掉它并留下可追溯的日志。 */
    private void ReleaseInheritedMarker(Session session, string trigger)
    {
        if (CompactionRegion.CloseInheritedCompactionMarker(session) is not { } closed)
            return;
        Ctx.LoggerFor(ServiceName).Warn(
            $"compaction ({trigger}): closed an interrupted compaction marker left by a previous process (end seq {closed.Seq})");
    }

    private static string Describe(TimeSpan elapsed)
        => elapsed <= TimeSpan.Zero
            ? "an unmeasured time"
            : elapsed < TimeSpan.FromMinutes(1)
                ? $"{elapsed.TotalSeconds:0.0}s"
                : $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s";

    private void LogPrune(PruneResult prune, string trigger)
    {
        if (prune.Pruned.Count == 0)
            return;
        Ctx.LoggerFor(ServiceName).Info(
            $"compaction ({trigger}): pruned {prune.Pruned.Count} oversized tool results, removed {prune.CharsRemoved} chars");
    }
}
