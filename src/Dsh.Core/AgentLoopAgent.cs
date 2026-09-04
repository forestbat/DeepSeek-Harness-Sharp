using System.Text.Json;
using Cordis;
using Dsh.Llm;
using Message = Dsh.Llm.Message;

namespace Dsh.Core;

public sealed class AgentLoopAgent : IAgent
{
    private abstract class Phase
    {
        public sealed class Idle : Phase
        {
            public int LastTurn { get; set; }
        }

        public sealed class Maintenance : Phase
        {
            public CancellationTokenSource Abort { get; } = new();
            public int LastTurn { get; set; }
            public bool WakeRequested { get; set; }
        }

        public sealed class Running : Phase
        {
            public CancellationTokenSource Abort { get; set; } = new();
            public int Turn { get; set; }
            public int Step { get; set; }
            public bool WakeRequested { get; set; }
        }
    }

    public enum StepOutcome
    {
        Completed,
        MaxTokens,
    }

    private readonly Context _loopCtx;
    private readonly Func<Session, int> _lastTurnOf;
    private Phase _phase;
    private Task _activityDone = Task.CompletedTask;
    private bool _requestHeaderLogged;
    private int? _requestSurfaceGeneration;
    private readonly RuntimeContextProjection _runtimeContext;

    public AgentLoopAgent(Context loopCtx, SessionId id, AgentOptions options, Session session, Func<Session, int> lastTurnOf)
    {
        _loopCtx = loopCtx;
        Id = id;
        Options = options;
        Session = session;
        _lastTurnOf = lastTurnOf;
        Dispatch = new AgentEventDispatch(loopCtx, this);
        ScopeKey = new ScopeKey();
        Ctx = DshScope.CreateScope(loopCtx, ScopeKey).Extend((AgentContextKey, this));
        Inbox = new Inbox(session, new InboxNotifications
        {
            Inserted = message => Dispatch.Emit(AgentEventNames.InboxInserted, new { Agent = this, Message = message }),
            Discarded = message => Dispatch.Emit(AgentEventNames.InboxDiscarded, new { Agent = this, Message = message }),
            Claimed = (message, turn) => Dispatch.Emit(AgentEventNames.InboxClaimed, new { Agent = this, Message = message, Turn = turn }),
        });
        _phase = new Phase.Idle { LastTurn = lastTurnOf(session) };
        _runtimeContext = new RuntimeContextProjection(Ctx, session);
    }

    internal static readonly object AgentContextKey = new();

    public SessionId Id { get; }
    public AgentOptions Options { get; }
    public Session Session { get; }
    public ScopeKey ScopeKey { get; }
    public Context Ctx { get; }
    public Inbox Inbox { get; }
    public AgentEventDispatch Dispatch { get; }

    public AgentStatus Status => _phase is Phase.Running ? AgentStatus.Running : AgentStatus.Idle;

    private void SetPhase(Phase next)
    {
        var previousStatus = Status;
        _phase = next;
        if (Status != previousStatus)
            Dispatch.Emit(AgentEventNames.Status, new { Agent = this, Status });
    }

    private static CancellationTokenSource? PhaseAbort(Phase phase) => phase switch
    {
        Phase.Maintenance maintenance => maintenance.Abort,
        Phase.Running running => running.Abort,
        _ => null,
    };

    public void Send(UserMessage message, string target, bool wakeup)
    {
        var wakingAfterAbort = wakeup
            && _phase is not Phase.Idle
            && PhaseAbort(_phase)?.IsCancellationRequested == true;
        var resolvedTarget = wakingAfterAbort ? InboxTargets.NextTurn : target;
        Inbox.Splice(resolvedTarget, int.MaxValue, 0, [message]);
        if (wakeup)
            WakeDriver(wakingAfterAbort);
    }

    public void Followup(UserMessage message) => Send(message, InboxTargets.NextTurn, true);

    public void Steer(UserMessage message) => Send(message, InboxTargets.NextStep, true);

    public void Inject(UserMessage message) => Send(message, InboxTargets.NextStep, false);

    public void Cancel(AgentCancelCause cause, bool keepInbox = false)
    {
        if (!keepInbox)
        {
            Inbox.Clear();
            switch (_phase)
            {
                case Phase.Maintenance maintenance:
                    maintenance.WakeRequested = false;
                    break;
                case Phase.Running running:
                    running.WakeRequested = false;
                    break;
            }
        }
        PhaseAbort(_phase)?.Cancel();
    }

    public async Task<T> RunMaintenance<T>(Func<CancellationToken, Task<T>> job)
    {
        if (_phase is not Phase.Idle idle)
            throw new InvalidOperationException($"agent \"{Id}\" already has active work");
        var maintenance = new Phase.Maintenance { LastTurn = idle.LastTurn };
        SetPhase(maintenance);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activityDone = done.Task;
        try
        {
            return await job(maintenance.Abort.Token);
        }
        finally
        {
            SetPhase(new Phase.Idle { LastTurn = maintenance.LastTurn });
            if (maintenance.WakeRequested && Inbox.HasPending)
                WakeDriver();
            done.SetResult();
        }
    }

    private void WakeDriver(bool wakeAfterAbort = false)
    {
        if (_phase is not Phase.Idle idle)
        {
            switch (_phase)
            {
                case Phase.Maintenance maintenancePhase:
                    maintenancePhase.WakeRequested = true;
                    break;
                case Phase.Running runningPhase when wakeAfterAbort && runningPhase.Abort.IsCancellationRequested:
                    runningPhase.WakeRequested = true;
                    break;
            }
            return;
        }
        var running = new Phase.Running { Turn = idle.LastTurn, Step = 0 };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activityDone = done.Task;
        SetPhase(running);
        Kick(running).ContinueWith(task => done.SetResult(), TaskContinuationOptions.ExecuteSynchronously);
    }

    public async Task WhenIdle()
    {
        while (true)
        {
            var activity = _activityDone;
            await activity;
            if (ReferenceEquals(activity, _activityDone))
                return;
        }
    }

    private Exception ReportError(Exception error)
    {
        var turn = _phase switch
        {
            Phase.Running running => running.Turn,
            Phase.Idle idle => idle.LastTurn,
            Phase.Maintenance maintenance => maintenance.LastTurn,
            _ => 0,
        };
        var step = _phase is Phase.Running runningStep ? runningStep.Step : 0;
        Dispatch.Emit(AgentEventNames.Error, new { Agent = this, Turn = turn, Step = step, Error = error });
        return error;
    }

    private async Task Kick(Phase.Running running)
    {
        try
        {
            while (await Turn(running))
            {
            }
        }
        catch
        {
            // Reported failures and cancellation are contained at the driver boundary.
        }
        finally
        {
            if (ReferenceEquals(_phase, running))
            {
                var wakeRequested = running.WakeRequested;
                SetPhase(new Phase.Idle { LastTurn = running.Turn });
                if (wakeRequested && Inbox.HasPending)
                    WakeDriver();
            }
        }
    }

    private async Task<PreStepDecision> PreStep(Phase.Running phase, string target)
    {
        var signal = phase.Abort.Token;
        var claimed = Inbox.Claim(target, phase.Turn);
        var systemPrompt = _loopCtx.Get<SystemPrompt>(SystemPrompt.ServiceName)
            ?? throw new InvalidOperationException("agent loop requires the systemPrompt service");
        var assembly = await systemPrompt.Assemble(new AssembleContext(ScopeKey, signal));
        signal.ThrowIfCancellationRequested();
        var sections = PromptRender.RenderContextSections(assembly);
        var context = _runtimeContext.Project(PromptRender.JoinContextSections(sections), sections);
        var payload = new PreStepPayload(this, claimed, phase.Turn, phase.Step + 1, signal);
        var decision = await Dispatch.Waterfall(
            AgentEventNames.PreStep,
            payload,
            () => new ValueTask<object?>(new PreStepDecision.Enter(
                context is null ? payload.Messages : [..payload.Messages, context]))) as PreStepDecision;
        signal.ThrowIfCancellationRequested();
        return decision ?? new PreStepDecision.Enter(claimed);
    }

    private async Task<bool> Turn(Phase.Running phase)
    {
        var signal = phase.Abort.Token;
        signal.ThrowIfCancellationRequested();
        var turn = phase.Turn + 1;
        TurnEndReason? turnEnds = null;
        try
        {
            Session.Append(new TurnStartPayload(turn));
        }
        catch (Exception error)
        {
            throw ReportError(error);
        }
        phase.Turn = turn;
        var target = InboxTargets.NextTurn;
        try
        {
            while (true)
            {
                signal.ThrowIfCancellationRequested();
                var decision = await PreStep(phase, target);
                if (decision is PreStepDecision.Reject)
                {
                    turnEnds = new TurnEndReason.Blocked();
                    return false;
                }
                var enter = (PreStepDecision.Enter)decision;
                if (turnEnds is not null && enter.Messages.Count == 0)
                    break;
                if (phase.Step == 0 && enter.Messages.Count == 0)
                {
                    turnEnds = new TurnEndReason.Completed();
                    return false;
                }
                signal.ThrowIfCancellationRequested();
                var step = phase.Step + 1;
                Session.Append(new StepStartPayload(turn, step));
                phase.Step = step;
                try
                {
                    foreach (var message in enter.Messages)
                        Session.Append(new UserMessagePayload(message), new SurfaceOp.Append());
                    var stepEnd = await Step(phase, enter.StartsRequestSeries);
                    if (turnEnds is not TurnEndReason.MaxTokens)
                        turnEnds = stepEnd switch
                        {
                            StepOutcome.Completed => new TurnEndReason.Completed(),
                            StepOutcome.MaxTokens => new TurnEndReason.MaxTokens(),
                            _ => null,
                        };
                }
                finally
                {
                    Session.Append(new StepEndPayload(turn, step));
                }
                signal.ThrowIfCancellationRequested();
                if (turnEnds is not null && Inbox.NextStep.Count == 0)
                {
                    await Dispatch.Serial(AgentEventNames.TurnStopping, new AgentTurnStoppingPayload(this, turn, signal));
                    signal.ThrowIfCancellationRequested();
                }
                if (turnEnds is not null && Inbox.NextStep.Count == 0)
                    break;
                target = InboxTargets.NextStep;
            }
        }
        catch (Exception error)
        {
            if (signal.IsCancellationRequested)
            {
                turnEnds = new TurnEndReason.Aborted(new AgentCancelCause.User());
                throw;
            }
            turnEnds = new TurnEndReason.Error(error is LlmException llm
                ? llm.Failure
                : new LlmFailure(LlmFailureClassifiers.ErrorChain(error), "UNKNOWN"));
            throw ReportError(error);
        }
        finally
        {
            try
            {
                Session.Append(new TurnEndPayload(turn, turnEnds ?? new TurnEndReason.Error(new LlmFailure("turn ended without a reason", "UNKNOWN"))));
            }
            catch (Exception error)
            {
                ReportError(error);
            }
        }
        if (!Inbox.HasPending)
            return false;
        phase.Abort.Dispose();
        phase.Abort = new CancellationTokenSource();
        phase.WakeRequested = false;
        phase.Step = 0;
        return true;
    }

    private async Task<StepOutcome?> Step(Phase.Running phase, bool startsRequestSeries)
    {
        var signal = phase.Abort.Token;
        signal.ThrowIfCancellationRequested();
        var (turn, step) = (phase.Turn, phase.Step);
        var systemPrompt = _loopCtx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var assembly = await systemPrompt.Assemble(new AssembleContext(ScopeKey, signal));
        var system = PromptRender.RenderPrompt(assembly);

        while (true)
        {
            var surfaceGeneration = Session.SurfaceManager.ReplaceGeneration;
            var (request, preparedCall) = await BuildRequest(phase, assembly.Tools, system, Session.DeriveMessages(), startsRequestSeries, surfaceGeneration, signal);
            startsRequestSeries = false;
            var assembler = new BlockAssembler();
            var chunkSeqs = new List<long>();
            try
            {
                var stream = preparedCall is not null
                    ? preparedCall.Stream(request, signal)
                    : throw new InvalidOperationException("no LLM adapter prepared the request");
                await foreach (var chunk in stream.WithCancellation(signal))
                {
                    signal.ThrowIfCancellationRequested();
                    chunkSeqs.Add(Session.Append(new AssistantChunkPayload(turn, step, chunk)).Seq);
                    assembler.Push(chunk);
                }
                signal.ThrowIfCancellationRequested();
            }
            catch (Exception)
            {
                if (signal.IsCancellationRequested)
                {
                    var content = assembler.InterruptedBlocks();
                    if (content.Count > 0)
                    {
                        Session.Append(new AssistantMessagePayload(
                            turn,
                            step,
                            MessageFactory.CreateAssistantMessage(content, request.Provider, request.Model),
                            assembler.Usage,
                            true), new SurfaceOp.Append(), chunkSeqs);
                    }
                }
                throw;
            }

            var finish = assembler.Finish;
            if (finish is FinishReason.Error or FinishReason.Aborted)
            {
                var failure = finish switch
                {
                    FinishReason.Error error => error.Failure,
                    FinishReason.Aborted aborted => aborted.Failure,
                    _ => throw new InvalidOperationException(),
                };
                var action = await Dispatch.Waterfall(
                    AgentEventNames.RequestError,
                    new AgentRequestErrorPayload(this, turn, step, request.Provider, failure, preparedCall?.RetryPolicy, signal),
                    () => new ValueTask<object?>()) as RequestErrorAction;
                signal.ThrowIfCancellationRequested();
                if (action is not RequestErrorAction.Retry)
                    throw new LlmException(failure);
                continue;
            }

            var message = MessageFactory.CreateAssistantMessage(assembler.Blocks(), request.Provider, request.Model, assembler.ReplayState?.Response);
            Session.Append(
                new AssistantMessagePayload(turn, step, message, assembler.Usage),
                new SurfaceOp.Append(),
                chunkSeqs);
            if (finish is FinishReason.MaxTokens)
                return StepOutcome.MaxTokens;

            var toolCalls = message.Content.OfType<ToolCallBlock>().ToList();
            if (toolCalls.Count == 0)
                return StepOutcome.Completed;
            var concluded = await ExecuteToolCalls(phase, turn, step, toolCalls, signal);
            return concluded ? StepOutcome.Completed : null;
        }
    }

    private async Task<(GenerateOptions Request, PreparedLlmCall? PreparedCall)> BuildRequest(
        Phase.Running phase,
        IReadOnlyList<ToolSchema> tools,
        string system,
        IReadOnlyList<Message> boundaryMessages,
        bool startsRequestSeries,
        int surfaceGeneration,
        CancellationToken signal)
    {
        var session = Session;
        var persistedHeader = session.RequestHeader();
        var persistedConfig = persistedHeader?.Config;
        var provider = Options.Provider ?? "";
        var model = Options.Model ?? "";
        var persistedReasoningEffort = persistedConfig is not null
            && persistedConfig.Provider == provider
            && persistedConfig.Model == model
            && persistedHeader?.AdapterDefaults?.ReasoningEffort != true
                ? persistedConfig.ReasoningEffort
                : null;
        var reasoningEffort = Options.ReasoningEffort ?? persistedReasoningEffort;
        var seedConfig = _requestHeaderLogged
            ? RequestProposal(persistedHeader!)
            : new LlmCallConfig(provider, model, reasoningEffort, null, Options.MaxTokens, null);
        var proposedConfig = await Dispatch.Waterfall(
            AgentEventNames.Request,
            new AgentRequestPayload(this, phase.Turn, phase.Step, signal),
            () => new ValueTask<object?>(seedConfig)) as LlmCallConfig ?? seedConfig;
        signal.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(proposedConfig.Provider) || string.IsNullOrEmpty(proposedConfig.Model))
        {
            throw new InvalidOperationException(
                $"agent \"{Id}\" has no provider/model: set AgentOptions.provider and AgentOptions.model or supply both via the agent/request waterfall");
        }
        var llm = _loopCtx.Get<LlmRuntime>(LlmRuntime.ServiceName)
            ?? throw new InvalidOperationException("agent loop requires the llm service");
        LlmCallConfig config;
        PreparedLlmCall? preparedCall;
        try
        {
            preparedCall = await llm.PrepareCall(proposedConfig, signal);
            config = preparedCall.Config;
        }
        catch (LlmException error) when (error.Code == LlmFailureCodes.NoAdapter)
        {
            preparedCall = null;
            config = proposedConfig;
        }
        signal.ThrowIfCancellationRequested();

        var header = RequestHeader.Canonicalize(new EpochHeader(
            config,
            preparedCall?.AdapterDefaults,
            string.IsNullOrEmpty(system) ? null : system,
            tools.Count > 0 ? tools : null));
        var baseline = session.RequestHeader();
        var startsSeries = startsRequestSeries || _requestSurfaceGeneration != surfaceGeneration;
        if (!_requestHeaderLogged)
        {
            session.Append(new RequestHeaderPayload(header, baseline is null ? RequestHeaderReasons.Initial : RequestHeaderReasons.Resume));
            _requestHeaderLogged = true;
        }
        else if (baseline is null || !RequestHeader.Equals(baseline, header))
        {
            session.Append(new RequestHeaderPayload(header, RequestHeaderReasons.Change, startsSeries));
        }
        else if (startsSeries)
        {
            session.Append(new RequestHeaderPayload(header, RequestHeaderReasons.Series));
        }
        _requestSurfaceGeneration = surfaceGeneration;

        var requestContext = new RequestContextPayload(config.Provider, config.Model, preparedCall?.ContextWindow);
        var previousContext = session.RequestContext();
        if (previousContext?.Provider != requestContext.Provider
            || previousContext.Model != requestContext.Model
            || previousContext.ContextWindow != requestContext.ContextWindow)
        {
            session.Append(requestContext);
        }
        signal.ThrowIfCancellationRequested();

        var request = AgentLoopRequestMarker.Mark(new GenerateOptions
        {
            Provider = config.Provider,
            Model = config.Model,
            ReasoningEffort = config.ReasoningEffort,
            Messages = boundaryMessages,
            System = header.System,
            Tools = header.Tools,
            Temperature = config.Temperature,
            MaxTokens = config.MaxTokens,
            Stop = config.Stop,
            SessionId = session.Id,
            Cancellation = signal,
        });
        return (request, preparedCall);
    }

    private static LlmCallConfig RequestProposal(EpochHeader header)
    {
        if (header.AdapterDefaults is null)
            return header.Config;
        return header.Config with
        {
            ReasoningEffort = header.AdapterDefaults.ReasoningEffort ? null : header.Config.ReasoningEffort,
            MaxTokens = header.AdapterDefaults.MaxTokens ? null : header.Config.MaxTokens,
        };
    }

    private async Task<bool> ExecuteToolCalls(Phase.Running phase, int turn, int step, List<ToolCallBlock> toolCalls, CancellationToken signal)
    {
        var tools = _loopCtx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("agent loop requires the tools service");
        var planned = toolCalls.Select(block => new ToolExecutionInput
        {
            CallId = block.Id,
            Name = block.Name,
            Arguments = ParseArguments(block.Arguments),
            RawArguments = block.Arguments,
            Agent = this,
            Signal = signal,
        }).ToList();

        var next = 0;
        var concluded = false;
        while (next < planned.Count)
        {
            var first = planned[next];
            var mode = tools.ExecutionModeKind(first);
            var group = mode == ToolExecutionModeKind.Parallel ? planned.GetRange(next, planned.Count - next) : [first];
            var (consumed, aborted, groupConcluded) = await RunGroup(tools, turn, step, group, mode, signal);
            next += consumed;
            concluded = concluded || groupConcluded;
            if (aborted)
            {
                foreach (var call in planned.GetRange(next, planned.Count - next))
                    AppendSkippedToolCall(turn, step, call);
                return concluded;
            }
        }
        return concluded;
    }

    private async Task<(int Consumed, bool Aborted, bool Concluded)> RunGroup(
        ToolRuntime tools,
        int turn,
        int step,
        List<ToolExecutionInput> group,
        ToolExecutionModeKind mode,
        CancellationToken signal)
    {
        const int maxParallel = 10;
        var slots = new (ToolRunContext Exec, ToolExecutionResult Result, bool NeedsPost)?[group.Count];
        var callSeqs = new long?[group.Count];
        var nextToStart = 0;
        var committed = 0;
        var started = 0;
        var aborted = signal.IsCancellationRequested;
        var concluded = false;
        Exception? schedulerFailure = null;

        async Task CommitReady()
        {
            while (committed < group.Count)
            {
                var slot = slots[committed];
                if (slot is null)
                    break;
                var result = slot.Value.NeedsPost
                    ? await tools.FinalizeScheduledExecution(slot.Value.Exec, slot.Value.Result)
                    : tools.FinishScheduledExecution(slot.Value.Exec, slot.Value.Result);
                AppendToolResult(turn, step, group[committed], result, callSeqs[committed]!.Value);
                foreach (var context in result.AdditionalContexts ?? [])
                    Inbox.Splice(InboxTargets.NextStep, Inbox.NextStep.Count, 0, [context]);
                concluded = concluded || result is ToolExecutionResult.Success { ConcludesTurn: true };
                committed++;
            }
        }

        var inFlight = new Dictionary<int, Task<int>>();

        async Task StartCall(int index)
        {
            callSeqs[index] = Session.Append(new ToolCallPayload(turn, step, group[index].CallId, group[index].Name, group[index].RawArguments)).Seq;
            started++;
            var prepared = await tools.PrepareScheduledExecution(group[index]);
            if (schedulerFailure is not null)
                throw schedulerFailure;
            switch (prepared)
            {
                case ScheduledToolPreparation.Dispatch dispatch:
                    inFlight[index] = Task.Run(async () =>
                    {
                        try
                        {
                            var outcome = await tools.DispatchScheduledExecution(dispatch.Exec);
                            slots[index] = outcome switch
                            {
                                ScheduledToolDispatch.PostResult postResult => (dispatch.Exec, postResult.Result, true),
                                ScheduledToolDispatch.FinalResult finalResult => (dispatch.Exec, finalResult.Result, false),
                                _ => throw new InvalidOperationException(),
                            };
                            return index;
                        }
                        catch (Exception error)
                        {
                            schedulerFailure ??= error;
                            return index;
                        }
                    });
                    break;
                case ScheduledToolPreparation.PostResult postResult:
                    slots[index] = (postResult.Exec, postResult.Result, true);
                    break;
                case ScheduledToolPreparation.FinalResult finalResult:
                    slots[index] = (finalResult.Exec, finalResult.Result, false);
                    break;
            }
        }

        async Task FillPool()
        {
            while (!aborted && nextToStart < group.Count && inFlight.Count < maxParallel)
            {
                if (nextToStart > 0 && mode == ToolExecutionModeKind.Parallel
                    && tools.ExecutionModeKind(group[nextToStart]) != ToolExecutionModeKind.Parallel)
                    break;
                await StartCall(nextToStart);
                nextToStart++;
                if (schedulerFailure is not null)
                    throw schedulerFailure;
                await CommitReady();
                if (signal.IsCancellationRequested)
                    aborted = true;
            }
        }

        await FillPool();
        while (inFlight.Count > 0)
        {
            var settled = await Task.WhenAny(inFlight.Values);
            var settledIndex = await settled;
            inFlight.Remove(settledIndex);
            if (schedulerFailure is not null)
                throw schedulerFailure;
            await CommitReady();
            if (signal.IsCancellationRequested)
                aborted = true;
            await FillPool();
        }

        if (aborted)
        {
            for (var index = started; index < group.Count; index++)
                AppendSkippedToolCall(turn, step, group[index]);
            return (group.Count, true, concluded);
        }
        if (committed != started)
            throw new InvalidOperationException("tool-call scheduler: uncommitted settled calls");
        return (started, false, concluded);
    }

    private void AppendSkippedToolCall(int turn, int step, ToolExecutionInput call)
    {
        var callSeq = Session.Append(new ToolCallPayload(turn, step, call.CallId, call.Name, call.RawArguments)).Seq;
        AppendToolResult(turn, step, call, ToolRuntime.AbortedBeforeDispatchResult(), callSeq);
    }

    private void AppendToolResult(int turn, int step, ToolExecutionInput call, ToolExecutionResult result, long callSeq)
    {
        var message = MessageFactory.CreateToolResultMessage(call.CallId, result.Content, result.IsError);
        Session.Append(new ToolResultPayload(
            turn,
            step,
            message,
            result is ToolExecutionResult.Failure { Error.Info: { } info } ? new ToolResultErrorInfo(info.Name, info.Code) : null,
            result.Meta), new SurfaceOp.Append(), [callSeq]);
    }

    private static JsonElement ParseArguments(string raw)
    {
        try
        {
            return string.IsNullOrEmpty(raw)
                ? JsonDocument.Parse("{}").RootElement
                : JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(raw);
        }
    }
}
