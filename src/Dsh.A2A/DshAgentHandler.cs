using System.Text;
using A2A;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using A2AMessage = A2A.Message;
using A2ARole = A2A.Role;
using A2APart = A2A.Part;
using Dsh.Agent;

namespace Dsh.A2A;

/**
 * DSH 侧 A2A agent 处理器: 把 SDK 的 IAgentHandler 桥到 AgentRegistry/IAgent。
 * 任务会话固定走会话档(AgentRegistry.Create), contextId 即 DSH SessionId, 事件翻译由 SDK 的 TaskUpdater 产出。
 */
public sealed class DshAgentHandler : IAgentHandler, IDisposable
{
    private sealed class SessionRecord
    {
        public required AgentHandle Handle { get; init; }
        public required IAgent Agent { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly Context _ctx;
    private readonly string? _provider;
    private readonly string? _model;
    private readonly Dictionary<string, SessionRecord> _sessions = [];
    /** task id -> 正在执行的任务所属会话: 取消请求只带 task id。 */
    private readonly Dictionary<string, SessionRecord> _byTask = [];
    private readonly Lock _gate = new();

    public DshAgentHandler(Context ctx, string? provider = null, string? model = null)
    {
        _ctx = ctx;
        _provider = provider;
        _model = model;
    }

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        try
        {
            var text = context.UserText;
            if (string.IsNullOrEmpty(text))
                throw new A2AException("message has no text parts", A2AErrorCode.ContentTypeNotSupported);
            var record = await ResolveSessionAsync(context, cancellationToken);
            // 取消请求只带 task id, 不带 contextId —— 没有这张表, 取消就找不到正在跑的 agent,
            // 任务状态会翻成 canceled 而底下的 LLM 请求照样在跑, 拆机时就会一直等它。
            lock (_gate)
                _byTask[context.TaskId] = record;
            await updater.SubmitAsync(cancellationToken: cancellationToken);
            await updater.StartWorkAsync(cancellationToken: cancellationToken);
            await record.Gate.WaitAsync(cancellationToken);
            try
            {
                var startSeq = record.Agent.Session.Seq;
                record.Agent.Followup(MessageFactory.CreateUserText(text));
                await record.Agent.WhenIdle();
                var outcome = CollectOutcome(record, startSeq);
                if (outcome.Canceled)
                {
                    await updater.CancelAsync(cancellationToken: cancellationToken);
                    return;
                }
                if (outcome.Error is { } error)
                {
                    await updater.FailAsync(AgentMessage(error), cancellationToken: cancellationToken);
                    return;
                }
                await updater.CompleteAsync(outcome.Text is { Length: > 0 } final ? AgentMessage(final) : null, cancellationToken: cancellationToken);
            }
            finally
            {
                lock (_gate)
                {
                    if (_byTask.TryGetValue(context.TaskId, out var current) && ReferenceEquals(current, record))
                        _byTask.Remove(context.TaskId);
                }
                record.Gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            await updater.CancelAsync(cancellationToken: CancellationToken.None);
        }
        catch (A2AException)
        {
            throw;
        }
        catch (Exception error)
        {
            await updater.FailAsync(AgentMessage($"agent run failed: {error.Message}"), cancellationToken: CancellationToken.None);
        }
    }

    /**
     * 让这个任务正在跑的 agent 停手。返回是否找到并取消了它。
     *
     * 取消请求只带 task id, 不带 contextId, 所以查找必须走 task 表;
     * 而且必须在 A2A SDK 的取消路径之前调用 —— SDK 那一步要与事件通知器抢锁,
     * 而锁正被卡在 LLM 请求上的执行体持有; 不先解开这一步, 取消就会一直等下去。
     */
    public bool CancelAgentForTask(string taskId)
    {
        SessionRecord? record;
        lock (_gate)
            _byTask.TryGetValue(taskId, out record);
        if (record is null)
            return false;
        record.Agent.Cancel(new AgentCancelCause.User());
        return true;
    }

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        CancelAgentForTask(context.TaskId);
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.CancelAsync(cancellationToken: cancellationToken);
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
            record.Agent.Cancel(new AgentCancelCause.Disposed());
            record.Handle.Dispose.Dispose();
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
        if (context.IsContinuation || context.ClientProvidedContextId)
            throw new A2AException($"unknown contextId: {context.ContextId}", A2AErrorCode.InvalidParams);
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
            ?? throw new A2AException("the A2A handler requires the agents service", A2AErrorCode.InternalError);
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create(context.ContextId),
            Cwd(),
            new AgentOptions(_provider, _model)), cancellationToken);
        var record = new SessionRecord { Handle = handle, Agent = handle.Agent };
        lock (_gate)
            _sessions[context.ContextId] = record;
        return record;
    }

    private string Cwd()
        => (_ctx.GetProp("harnessOptions") as HarnessOptions)?.Cwd ?? Directory.GetCurrentDirectory();

    private static (string? Text, string? Error, bool Canceled) CollectOutcome(SessionRecord record, long startSeq)
    {
        var events = record.Agent.Session.SnapshotEvents(startSeq);
        TurnEndReason? reason = null;
        var text = new StringBuilder();
        foreach (var sessionEvent in events)
        {
            switch (sessionEvent.Data)
            {
                case TurnEndPayload turnEnd:
                    reason = turnEnd.Reason;
                    break;
                case AssistantMessagePayload assistant:
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
