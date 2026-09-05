using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

public static class InProcessDriver
{
    public static Task<ISubagentRun> StartAsync(ResolvedSubagentStartRequest request, IReadOnlyList<SessionEvent>? seed)
    {
        DelegationDepth.AssertSubagentMaxDepth(request.Request.MaxDepth);
        if (request.Request.Signal.IsCancellationRequested)
            throw new InvalidOperationException("subagent request was aborted before child publication");
        var parent = request.Parent;
        var childDepth = DelegationDepth.ResolveChildDepth(parent, request.Request.MaxDepth);
        var childId = SessionId.Create(Guid.NewGuid().ToString());
        var activationBoundary = seed?.Count ?? 0;
        var inherited = ChildCompositionSupport.CaptureDelegatedPolicyOverrides(parent);
        var sessions = parent.Ctx.Get<SessionStore>(SessionStore.ServiceName)
            ?? throw new SubagentException(
                "in-process subagents require the sessions service", SubagentErrorCodes.SessionStoreUnavailable);
        var session = sessions.Create(
            childId,
            seed,
            ChildCompositionSupport.ChildSessionHeader(parent, childDepth, childId, seed is not null),
            seed is null ? null : activationBoundary);
        var child = new AgentLoopAgent(
            parent.Ctx.Root,
            childId,
            ChildCompositionSupport.ResolveChildAgentOptions(parent, request.Request.AgentOptions),
            session,
            LastTurnOf);
        (parent.Ctx.Get(SubagentRuntime.ServiceName, false) as SubagentRuntime)?.NoteChildScope(child.ScopeKey);
        var run = new InProcessRun(child, activationBoundary, request, inherited);
        parent.Ctx.Root.Emit(AgentEventNames.SessionStart, new { Agent = (IAgent)child, Source = "startup" });
        return Task.FromResult<ISubagentRun>(run);
    }

    private static int LastTurnOf(Session session)
    {
        var lastTurn = 0;
        foreach (var sessionEvent in session.SnapshotEvents())
        {
            if (sessionEvent.Data is TurnStartPayload turnStart)
                lastTurn = turnStart.Turn;
        }
        return lastTurn;
    }

    internal static SubagentStopReason ToStopReason(TurnEndReason reason) => reason switch
    {
        TurnEndReason.Completed => SubagentStopReason.Completed,
        TurnEndReason.MaxTokens => SubagentStopReason.MaxTokens,
        TurnEndReason.Aborted => SubagentStopReason.Aborted,
        TurnEndReason.Blocked => SubagentStopReason.Refusal,
        _ => SubagentStopReason.Error,
    };

    private sealed class InProcessRun : ISubagentRun
    {
        private readonly AgentLoopAgent _child;
        private readonly int _activationBoundary;
        private readonly List<IDisposable> _composition = [];
        private readonly CancellationTokenRegistration _abortRegistration;
        private readonly StructuredOutputAttachment? _structured;
        private volatile bool _cancelled;

        public InProcessRun(
            AgentLoopAgent child,
            int activationBoundary,
            ResolvedSubagentStartRequest request,
            DelegatedPolicyOverrides inherited)
        {
            _child = child;
            _activationBoundary = activationBoundary;
            ChildCompositionSupport.AppendDelegatedPolicyOverrides(child.Session, inherited);
            if (request.Request.OutputSchema is { } schema)
                _structured = StructuredOutputAttachment.Attach(child.Ctx, child, schema);
            _composition.Add(ChildCompositionSupport.ApplyChildComposition(
                child.Ctx,
                request.Parent,
                child,
                new ChildComposition(request.Request.Persona, request.Request.ToolFilter, request.Request.OutputSchema),
                _structured));
            AttachDescriptorAppend(child.Ctx, child.Session, request.Descriptor);
            _abortRegistration = request.Request.Signal.Register(() =>
            {
                _cancelled = true;
                child.Cancel(new AgentCancelCause.Parent());
            });
            Result = DriveAsync(request.Request);
        }

        public SessionId Id => _child.Id;

        public IAgent LocalAgent => _child;

        public Task<SubagentResult> Result { get; }

        private async Task<SubagentResult> DriveAsync(SubagentStartRequest request)
        {
            if (!_cancelled)
            {
                _child.Followup(MessageFactory.CreateUserMessage(request.Prompt));
                await _child.WhenIdle();
            }
            return ReadResult();
        }

        private SubagentResult ReadResult()
        {
            var own = _child.Session.SnapshotEvents(_activationBoundary);
            var folded = ConsumedWorkFold.Fold(own);
            var recorded = folded.End?.Data is TurnEndPayload turnEnd
                ? ToStopReason(turnEnd.Reason)
                : SubagentStopReason.Error;
            var output = AssistantOutput.FinalAssistantOutput(own) ?? [];
            var stopReason = _cancelled && recorded != SubagentStopReason.Completed
                ? SubagentStopReason.Aborted
                : recorded;
            if (_structured is null)
                return new SubagentResult { Output = output, StopReason = stopReason };
            if (_structured.Captured is { } captured)
                return new SubagentResult { Output = output, StopReason = stopReason, Structured = captured };
            if (stopReason == SubagentStopReason.Completed)
                stopReason = _cancelled ? SubagentStopReason.Aborted : SubagentStopReason.Error;
            return new SubagentResult { Output = output, StopReason = stopReason };
        }

        public async Task DisposeAsync()
        {
            _cancelled = true;
            await _abortRegistration.DisposeAsync();
            _child.Cancel(new AgentCancelCause.Disposed());
            try
            {
                await Result;
            }
            catch
            {
                // 结果通道自有运行故障的所有权；处置只负责退绕。
            }
            foreach (var disposable in _composition)
                disposable.Dispose();
            _structured?.Dispose();
        }

        private void AttachDescriptorAppend(Context childCtx, Session session, SubagentDescriptorPayload descriptor)
        {
            var appended = false;
            _composition.Add(new FuncDispose(childCtx.On(AgentEventNames.PreStep, async (thisArg, args) =>
            {
                var next = (Func<ValueTask<object?>>)args[^1]!;
                var decision = await next();
                if (!appended && decision is PreStepDecision.Enter)
                {
                    appended = true;
                    session.Append(descriptor);
                }
                return decision;
            })));
        }

        private sealed class FuncDispose(Func<bool> dispose) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                dispose();
            }
        }
    }
}
