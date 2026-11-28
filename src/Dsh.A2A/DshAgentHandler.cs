using System.Text;
using A2A;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using A2AMessage = A2A.Message;
using A2APart = A2A.Part;
using A2ARole = A2A.Role;

namespace Dsh.A2A;

/**
 * DSH 侧 A2A agent 处理器: 把 SDK 的 IAgentHandler 桥到 AgentRegistry/IPersistentAgentManager/IAgentMessageBus。
 * 优先解析既有 live/suspended AgentId 或 SessionId 并向其注入消息, 仅在 contextId 全新时创建新会话。
 */
public sealed class DshAgentHandler(Context ctx, string? provider = null, string? model = null)
    : IAgentHandler, IDisposable
{
    private sealed class SessionRecord
    {
        public required AgentId AgentId { get; init; }
        public SessionId? TargetSessionId { get; set; }
        public AgentHandle? Handle { get; init; }
        public IAgent? Agent { get; set; }
        public bool Owned { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class TaskExecution
    {
        public CancellationTokenSource Cts { get; } = new();
        public IAgent? Agent { get; set; }
        public MessageId? QueuedMessageId { get; set; }
        public int? ClaimedTurn { get; set; }
        public bool OwnsActiveTurn { get; set; }
    }

    private readonly Context _ctx = ctx;
    private readonly string? _provider = provider;
    private readonly string? _model = model;
    private readonly Dictionary<string, SessionRecord> _sessions = [];
    /** task id -> 任务执行隔离状态: 取消只停本任务, 等待与结果采集只认本任务认领的 turn。 */
    private readonly Dictionary<string, TaskExecution> _byTask = [];
    private readonly Lock _gate = new();

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        try
        {
            var text = context.UserText;
            if (string.IsNullOrEmpty(text))
                throw new A2AException("message has no text parts", A2AErrorCode.ContentTypeNotSupported);

            var record = await ResolveSessionAsync(context, cancellationToken).ConfigureAwait(false);
            var taskState = new TaskExecution();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, taskState.Cts.Token);
            var linkedToken = linkedCts.Token;
            lock (_gate)
                _byTask[context.TaskId] = taskState;
            try
            {
                await updater.SubmitAsync(cancellationToken: linkedToken).ConfigureAwait(false);
                await updater.StartWorkAsync(cancellationToken: linkedToken).ConfigureAwait(false);
                await record.Gate.WaitAsync(linkedToken).ConfigureAwait(false);
                try
                {
                    var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
                    var preLive = ResolveLiveAgent(record, agents);
                    if (preLive is not null)
                        await preLive.WhenIdle().WaitAsync(linkedToken).ConfigureAwait(false);
                    long? startSeq = preLive?.Session.Seq;

                    var (agent, effectiveStartSeq, queuedMessageId, rejectedReason) = await InjectMessageAsync(
                        record, context, text, preLive, startSeq, linkedToken).ConfigureAwait(false);
                    if (rejectedReason is not null)
                    {
                        await updater.FailAsync(AgentMessage(rejectedReason), cancellationToken: linkedToken).ConfigureAwait(false);
                        return;
                    }

                    lock (_gate)
                    {
                        taskState.Agent = agent;
                        taskState.QueuedMessageId = queuedMessageId;
                    }

                    if (linkedToken.IsCancellationRequested)
                    {
                        RemovePendingMessageIfUnclaimed(taskState);
                        throw new OperationCanceledException(linkedToken);
                    }

                    var claimedTurn = await TrackAndWaitTurnAsync(
                        agent!, taskState, effectiveStartSeq, queuedMessageId, linkedToken).ConfigureAwait(false);
                    var outcome = CollectOutcome(agent!, effectiveStartSeq, claimedTurn);
                    if (outcome.Canceled)
                    {
                        await updater.CancelAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    if (outcome.Error is { } error)
                    {
                        await updater.FailAsync(AgentMessage(error), cancellationToken: linkedToken).ConfigureAwait(false);
                        return;
                    }
                    await updater.CompleteAsync(
                        outcome.Text is { Length: > 0 } final ? AgentMessage(final) : null,
                        cancellationToken: linkedToken).ConfigureAwait(false);
                }
                finally
                {
                    record.Gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                RemovePendingMessageIfUnclaimed(taskState);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    if (_byTask.TryGetValue(context.TaskId, out var current) && ReferenceEquals(current, taskState))
                        _byTask.Remove(context.TaskId);
                }
                taskState.Cts.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            await updater.CancelAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (A2AException)
        {
            throw;
        }
        catch (Exception error)
        {
            await updater.FailAsync(AgentMessage($"agent run failed: {error.Message}"), cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /**
     * 让这个任务停手: 始终取消任务自身令牌; 仅当本任务已认领活动回合时才中断共享 agent, 否则只摘除尚未消费的排队消息。
     * 必须在 A2A SDK 的取消路径之前调用, 以免与事件通知器互相等待锁。
     */
    public bool CancelAgentForTask(string taskId)
    {
        TaskExecution? taskState;
        IAgent? agent;
        MessageId? queuedId;
        bool ownsActiveTurn;
        lock (_gate)
        {
            if (!_byTask.TryGetValue(taskId, out taskState))
                return false;
            taskState.Cts.Cancel();
            agent = taskState.Agent;
            queuedId = taskState.QueuedMessageId;
            ownsActiveTurn = taskState.OwnsActiveTurn;
        }
        if (agent is null)
            return true;
        if (ownsActiveTurn)
        {
            agent.Cancel(new AgentCancelCause.User(), keepInbox: true);
            return true;
        }
        if (queuedId is { } id && agent is AgentLoopAgent loop)
        {
            if (!loop.Inbox.Remove(id))
            {
                bool becameOwner;
                lock (_gate)
                    becameOwner = taskState.OwnsActiveTurn;
                if (becameOwner)
                    agent.Cancel(new AgentCancelCause.User(), keepInbox: true);
            }
        }
        return true;
    }

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        CancelAgentForTask(context.TaskId);
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.CancelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        List<SessionRecord> records;
        lock (_gate)
        {
            records = [.. _sessions.Values];
            _sessions.Clear();
            _byTask.Clear();
        }
        foreach (var record in records)
        {
            if (record.Owned && record.Handle is not null)
            {
                record.Agent?.Cancel(new AgentCancelCause.Disposed());
                record.Handle.Dispose.Dispose();
            }
            record.Gate.Dispose();
        }
    }

    private async Task<SessionRecord> ResolveSessionAsync(RequestContext context, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(context.ContextId, out var existing))
                return existing;
        }

        if (TryResolveExistingTarget(context.ContextId) is { } matched)
        {
            lock (_gate)
            {
                if (_sessions.TryGetValue(context.ContextId, out var existing))
                    return existing;
                _sessions[context.ContextId] = matched;
            }
            return matched;
        }

        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)
            ?? throw new A2AException("the A2A handler requires the agents service", A2AErrorCode.InternalError);
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create(context.ContextId),
            Cwd(),
            new AgentOptions(_provider, _model)), cancellationToken).ConfigureAwait(false);
        var created = new SessionRecord
        {
            AgentId = handle.Agent.AgentId,
            TargetSessionId = handle.Agent.Id,
            Handle = handle,
            Agent = handle.Agent,
            Owned = true,
        };
        lock (_gate)
        {
            if (_sessions.TryGetValue(context.ContextId, out var raced))
            {
                handle.Dispose.Dispose();
                return raced;
            }
            _sessions[context.ContextId] = created;
        }
        return created;
    }

    private SessionRecord? TryResolveExistingTarget(string contextId)
    {
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        var sessionId = SessionId.Create(contextId);
        var agentId = AgentId.Create(contextId);

        if (agents?.Get(sessionId) is { } bySession)
        {
            return new SessionRecord
            {
                AgentId = bySession.AgentId,
                TargetSessionId = bySession.Id,
                Agent = bySession,
                Owned = false,
            };
        }

        var byAgent = agents?.List().FirstOrDefault(agent =>
            agent.AgentId == agentId || agent.Session.Header.AgentId == agentId);
        if (byAgent is not null)
        {
            return new SessionRecord
            {
                AgentId = byAgent.AgentId,
                TargetSessionId = byAgent.Id,
                Agent = byAgent,
                Owned = false,
            };
        }

        var persistent = _ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false);
        if (persistent is null)
            return null;

        var profile = persistent.Get(agentId)
            ?? persistent.List().FirstOrDefault(item =>
                item.ActiveSessionId == sessionId || item.SessionIds.Contains(sessionId));
        if (profile is null)
            return null;

        var live = (profile.ActiveSessionId is { } activeId ? agents?.Get(activeId) : null)
            ?? agents?.List().FirstOrDefault(agent => agent.AgentId == profile.AgentId);
        return new SessionRecord
        {
            AgentId = profile.AgentId,
            TargetSessionId = live?.Id ?? profile.ActiveSessionId,
            Agent = live,
            Owned = false,
        };
    }

    private async Task<(IAgent? Agent, long StartSeq, MessageId? QueuedMessageId, string? RejectedReason)> InjectMessageAsync(
        SessionRecord record,
        RequestContext context,
        string text,
        IAgent? preLive,
        long? startSeq,
        CancellationToken cancellationToken)
    {
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        var persistent = _ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false);
        var bus = _ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);

        var liveAgent = preLive ?? ResolveLiveAgent(record, agents);
        startSeq ??= liveAgent?.Session.Seq;

        var sender = new MessagePrincipal(
            PrincipalKind.Agent,
            $"a2a:{context.ContextId}",
            DisplayName: "a2a-client",
            ChannelId: "a2a");

        if (bus is not null)
        {
            var envelope = new AgentEnvelope(
                EnvelopeId: $"a2a-{context.TaskId}",
                TargetAgentId: record.AgentId,
                TargetSessionId: liveAgent?.Id ?? record.TargetSessionId,
                Sender: sender,
                TrustLevel: MessageTrustLevel.ExternalAgent,
                Intent: AgentMessageIntent.Task,
                Body: text,
                CorrelationId: context.TaskId,
                IdempotencyKey: $"a2a:{context.TaskId}",
                CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                WakeIfSuspended: true);
            var delivery = await bus.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (delivery.Status == AgentDeliveryStatus.Rejected)
                return (null, 0, null, delivery.Reason ?? "inbound A2A message was rejected");

            liveAgent ??= (delivery.DeliveredSessionId is { } deliveredId ? agents?.Get(deliveredId) : null)
                ?? ResolveLiveAgent(record, agents);
            if (liveAgent is null && persistent is not null)
            {
                liveAgent = await persistent.WakeAsync(record.AgentId, cancellationToken).ConfigureAwait(false);
                _ = await bus.FlushOfflineAsync(record.AgentId, cancellationToken).ConfigureAwait(false);
            }
            if (liveAgent is null)
                return (null, 0, null, $"target agent \"{record.AgentId}\" could not be woken");

            record.Agent = liveAgent;
            record.TargetSessionId = liveAgent.Id;
            var effectiveStartSeq = startSeq ?? ResolveInjectedStartSeq(liveAgent.Session, context.TaskId);
            var queuedMessageId = ResolveQueuedMessageId(liveAgent.Session, effectiveStartSeq, context.TaskId);
            return (liveAgent, effectiveStartSeq, queuedMessageId, null);
        }

        if (liveAgent is null && persistent is not null)
        {
            liveAgent = await persistent.WakeAsync(record.AgentId, cancellationToken).ConfigureAwait(false);
            startSeq = liveAgent.Session.Seq;
        }
        if (liveAgent is null)
            return (null, 0, null, $"target agent \"{record.AgentId}\" is not live");

        record.Agent = liveAgent;
        record.TargetSessionId = liveAgent.Id;
        var attribution = new MessageAttribution(
            sender,
            MessageTrustLevel.ExternalAgent,
            AgentMessageIntent.Task,
            EnvelopeId: $"a2a-{context.TaskId}",
            CorrelationId: context.TaskId);
        var userMessage = MessageFactory.CreateUserText(text);
        if (liveAgent is AgentLoopAgent loopAgent)
            loopAgent.SetMessageAttribution(userMessage.Id, attribution);
        var seqBeforeSend = startSeq ?? liveAgent.Session.Seq;
        liveAgent.Followup(userMessage);
        return (liveAgent, seqBeforeSend, userMessage.Id, null);
    }

    private void RemovePendingMessageIfUnclaimed(TaskExecution taskState)
    {
        IAgent? agent;
        MessageId? queuedId;
        bool ownsActiveTurn;
        lock (_gate)
        {
            agent = taskState.Agent;
            queuedId = taskState.QueuedMessageId;
            ownsActiveTurn = taskState.OwnsActiveTurn;
        }
        if (!ownsActiveTurn && queuedId is { } id && agent is AgentLoopAgent loop)
            loop.Inbox.Remove(id);
    }

    private async Task<int> TrackAndWaitTurnAsync(
        IAgent agent,
        TaskExecution taskState,
        long startSeq,
        MessageId? queuedMessageId,
        CancellationToken cancellationToken)
    {
        var turnDone = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateLock = new Lock();
        int? activeTurn = null;
        int? claimedTurn = null;
        var ended = false;
        var lastScannedSeq = startSeq - 1;

        void ClaimTurn(int turn)
        {
            if (ended)
                return;
            claimedTurn ??= turn;
            lock (_gate)
            {
                taskState.ClaimedTurn = claimedTurn;
                taskState.OwnsActiveTurn = true;
            }
        }

        void ProcessEvent(SessionEvent sessionEvent)
        {
            switch (sessionEvent.Data)
            {
                case TurnStartPayload turnStart:
                    activeTurn = turnStart.Turn;
                    if (queuedMessageId is null && claimedTurn is null)
                        ClaimTurn(turnStart.Turn);
                    break;
                case UserMessagePayload userMessage when queuedMessageId is { } qid && userMessage.Message.Id == qid:
                    if (activeTurn is { } turn)
                        ClaimTurn(turn);
                    break;
                case TurnEndPayload turnEnd:
                    if (claimedTurn == turnEnd.Turn)
                    {
                        ended = true;
                        lock (_gate)
                            taskState.OwnsActiveTurn = false;
                        turnDone.TrySetResult(turnEnd.Turn);
                    }
                    if (activeTurn == turnEnd.Turn)
                        activeTurn = null;
                    break;
            }
        }

        void OnSessionAppended(Session _, SessionEvent sessionEvent)
        {
            lock (stateLock)
            {
                if (sessionEvent.Seq <= lastScannedSeq)
                    return;
                lastScannedSeq = sessionEvent.Seq;
                ProcessEvent(sessionEvent);
            }
        }

        var offClaimed = agent.Ctx.On<AgentInboxClaimedNotification>(notification =>
        {
            if (!ReferenceEquals(notification.Agent, agent) || queuedMessageId is not { } qid || notification.Message.Id != qid)
                return;
            lock (stateLock)
                ClaimTurn(notification.Turn);
        });
        agent.Session.Appended += OnSessionAppended;
        try
        {
            lock (stateLock)
            {
                foreach (var sessionEvent in agent.Session.SnapshotEvents(startSeq))
                {
                    if (sessionEvent.Seq <= lastScannedSeq)
                        continue;
                    lastScannedSeq = sessionEvent.Seq;
                    ProcessEvent(sessionEvent);
                }
                if (!ended && claimedTurn is null && activeTurn is { } runningTurn
                    && queuedMessageId is { } qid && agent is AgentLoopAgent loop
                    && !IsMessagePendingInInbox(loop.Inbox, qid)
                    && !cancellationToken.IsCancellationRequested)
                {
                    ClaimTurn(runningTurn);
                }
            }
            return await turnDone.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            agent.Session.Appended -= OnSessionAppended;
            offClaimed();
        }
    }

    private static bool IsMessagePendingInInbox(Inbox inbox, MessageId messageId)
        => inbox.NextTurn.Any(message => message.Id == messageId)
            || inbox.NextStep.Any(message => message.Id == messageId);

    private static MessageId? ResolveQueuedMessageId(Session session, long startSeq, string taskId)
    {
        var envelopeId = $"a2a-{taskId}";
        var events = session.SnapshotEvents(startSeq);
        var seenEnvelope = false;
        foreach (var sessionEvent in events)
        {
            if (sessionEvent.Data is AgentEnvelopePayload payload && payload.Envelope.EnvelopeId == envelopeId)
            {
                seenEnvelope = true;
                continue;
            }
            if (seenEnvelope && sessionEvent.Data is InboxSplicePayload { Inserted.Count: > 0 } splice)
                return splice.Inserted[0].Id;
        }
        foreach (var sessionEvent in events)
        {
            if (sessionEvent.Data is InboxSplicePayload { Inserted.Count: > 0 } splice)
                return splice.Inserted[0].Id;
        }
        return null;
    }

    private static IAgent? ResolveLiveAgent(SessionRecord record, AgentRegistry? agents)
    {
        if (record.Agent is { } current && (agents is null || agents.Get(current.Id) is not null))
            return current;
        if (agents is null)
            return record.Agent;
        if (record.TargetSessionId is { } targetSession && agents.Get(targetSession) is { } bySession)
            return bySession;
        return agents.List().FirstOrDefault(agent =>
            agent.AgentId == record.AgentId || agent.Session.Header.AgentId == record.AgentId);
    }

    private static long ResolveInjectedStartSeq(Session session, string taskId)
    {
        var envelopeId = $"a2a-{taskId}";
        var events = session.SnapshotEvents();
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Data is AgentEnvelopePayload payload && payload.Envelope.EnvelopeId == envelopeId)
                return events[i].Seq;
        }
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Data is TurnStartPayload)
                return events[i].Seq;
        }
        return 0;
    }

    private string Cwd()
        => (_ctx.GetProp("harnessOptions") as HarnessOptions)?.Cwd ?? Directory.GetCurrentDirectory();

    private static (string? Text, string? Error, bool Canceled) CollectOutcome(IAgent agent, long startSeq, int claimedTurn)
    {
        var events = agent.Session.SnapshotEvents(startSeq);
        TurnEndReason? reason = null;
        var text = new StringBuilder();
        foreach (var sessionEvent in events)
        {
            switch (sessionEvent.Data)
            {
                case TurnEndPayload turnEnd when turnEnd.Turn == claimedTurn:
                    reason = turnEnd.Reason;
                    break;
                case AssistantMessagePayload assistant when assistant.Turn == claimedTurn:
                    foreach (var block in assistant.Message.Content.OfType<TextBlock>())
                        text.Append(block.Text);
                    break;
            }
        }
        if (reason is TurnEndReason.Interrupted or TurnEndReason.Aborted)
            return (null, null, true);
        if (reason is not TurnEndReason.Completed and not TurnEndReason.MaxTokens)
            return (null, $"turn ended without completion ({reason?.ToString() ?? "no turn end"})", false);
        return (text.ToString(), null, false);
    }

    private static A2AMessage AgentMessage(string text)
        => new()
        {
            Role = A2ARole.Agent,
            MessageId = Guid.NewGuid().ToString("N"),
            Parts = [A2APart.FromText(text)],
        };
}
