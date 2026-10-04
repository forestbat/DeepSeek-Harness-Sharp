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
    private readonly Lock _gate = new();
    private bool _disposed;

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

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        SessionRecord? record;
        lock (_gate)
            _sessions.TryGetValue(context.ContextId, out record);
        record?.Agent.Cancel(new AgentCancelCause.User());
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.CancelAsync(cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        _disposed = true;
        List<SessionRecord> records;
        lock (_gate)
        {
            records = [.. _sessions.Values];
            _sessions.Clear();
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
