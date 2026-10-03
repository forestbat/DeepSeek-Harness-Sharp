using System.Text;
using System.Text.Json.Nodes;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Subagent;

namespace Dsh.A2A;

/** A2A ↔ DSH 会话桥：任务会话固定走会话档（AgentRegistry.Create），事件翻译为 A2A 帧。 */
public sealed class A2aSessionBridge : IDisposable
{
    private const string FinalMessageArtifactId = "final-message";

    private sealed class SessionRecord
    {
        public required AgentHandle Handle { get; init; }
        public required IAgent Agent { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<Func<bool>> Removers { get; } = [];
        public string? ActiveTaskId { get; set; }
    }

    private static readonly IReadOnlySet<string> AffirmativeAnswers = new HashSet<string>(StringComparer.Ordinal)
    {
        "y", "yes", "ok", "approve", "approved", "allow", "allowed", "confirm", "agree",
        "是", "好", "同意", "允许", "确认",
    };

    private readonly Context _ctx;
    private readonly string? _provider;
    private readonly string? _model;
    private readonly A2aTaskTracker _tracker = new();
    private readonly Dictionary<string, SessionRecord> _sessions = [];
    private readonly Lock _gate = new();
    private readonly List<Func<bool>> _removers = [];
    private bool _disposed;

    public A2aSessionBridge(Context ctx, string? provider = null, string? model = null)
    {
        _ctx = ctx;
        _provider = provider;
        _model = model;
        _removers.Add(ctx.On<SessionEventNotification>(OnSessionEvent, new EventOptions { Global = true }));
    }

    public A2aTaskTracker Tracker => _tracker;

    public async Task<(A2aTask Task, A2aStreamHandle? Stream)> SendAsync(
        A2aSendMessageParams parameters, bool streaming, CancellationToken signal)
    {
        var message = parameters.Message;
        var text = ExtractText(message);
        var record = await ResolveSessionAsync(message.ContextId, signal);
        var contextId = record.Agent.Id.Value;
        if (_tracker.TryAnswer(contextId, text) is { } answered)
            return (_tracker.Require(answered), streaming ? _tracker.SubscribeStream(answered) : null);
        if (message.TaskId is { } taskId)
        {
            _ = _tracker.Require(taskId);
            throw new A2aException(A2aErrorCodes.UnsupportedOperation,
                $"task {taskId} is not awaiting input and cannot accept a new message");
        }
        var userMessage = new A2aMessage(
            string.IsNullOrEmpty(message.MessageId) ? Guid.NewGuid().ToString("N") : message.MessageId,
            A2aRoles.User, message.Parts, contextId);
        var newTaskId = _tracker.CreateTask(contextId, userMessage);
        _ = RunPromptAsync(record, newTaskId, MessageFactory.CreateUserText(text));
        return (_tracker.Require(newTaskId), streaming ? _tracker.SubscribeStream(newTaskId) : null);
    }

    public A2aTask GetTask(A2aGetTaskParams parameters) => _tracker.Require(parameters.Id, parameters.HistoryLength);

    public A2aListTasksResult ListTasks(A2aListTasksParams parameters) => _tracker.List(parameters);

    public Task<A2aTask> CancelTaskAsync(A2aTaskIdParams parameters)
    {
        SessionRecord? record;
        _tracker.RequestCancel(parameters.Id);
        lock (_gate)
            _sessions.TryGetValue(ContextOfTask(parameters.Id), out record);
        record?.Agent.Cancel(new AgentCancelCause.User());
        return Task.FromResult(_tracker.Require(parameters.Id));
    }

    public A2aStreamHandle Resubscribe(A2aTaskIdParams parameters)
        => _tracker.SubscribeStream(parameters.Id);

    public void Dispose()
    {
        _disposed = true;
        foreach (var remove in _removers)
            remove();
        _removers.Clear();
        List<SessionRecord> records;
        lock (_gate)
        {
            records = [.. _sessions.Values];
            _sessions.Clear();
        }
        foreach (var record in records)
        {
            record.Agent.Cancel(new AgentCancelCause.Disposed());
            foreach (var remove in record.Removers)
                remove();
            record.Handle.Dispose.Dispose();
            record.Gate.Dispose();
        }
    }

    private string ContextOfTask(string taskId) => _tracker.Require(taskId).ContextId;

    private async Task<SessionRecord> ResolveSessionAsync(string? contextId, CancellationToken signal)
    {
        if (contextId is not null)
        {
            lock (_gate)
            {
                return _sessions.TryGetValue(contextId, out var existing)
                    ? existing
                    : throw new A2aException(A2aErrorCodes.InvalidParams, $"unknown contextId: {contextId}");
            }
        }
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
            ?? throw new A2aException(A2aErrorCodes.InternalError, "the A2A bridge requires the agents service");
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create(Guid.NewGuid().ToString()),
            Cwd(),
            new AgentOptions(_provider, _model)), signal);
        var record = new SessionRecord { Handle = handle, Agent = handle.Agent };
        HookSessionEvents(record);
        lock (_gate)
            _sessions[handle.Agent.Id.Value] = record;
        return record;
    }

    private string Cwd()
        => (_ctx.GetProp("harnessOptions") as HarnessOptions)?.Cwd ?? Directory.GetCurrentDirectory();

    private static string ExtractText(A2aMessage message)
    {
        var builder = new StringBuilder();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case A2aTextPart text:
                    builder.Append(text.Text);
                    break;
                case A2aDataPart data:
                    builder.Append(data.Data.GetRawText());
                    break;
                case A2aFilePart:
                    throw new A2aException(A2aErrorCodes.ContentTypeNotSupported, "file parts are not supported");
            }
        }
        if (builder.Length == 0)
            throw new A2aException(A2aErrorCodes.InvalidParams, "message has no text parts");
        return builder.ToString();
    }

    private async Task RunPromptAsync(SessionRecord record, string taskId, UserMessage prompt)
    {
        try
        {
            await record.Gate.WaitAsync(_tracker.CancelTokenOf(taskId));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        long startSeq;
        try
        {
            if (_disposed || _tracker.IsTerminalTask(taskId))
                return;
            record.ActiveTaskId = taskId;
            startSeq = record.Agent.Session.Seq;
            _tracker.SetStatus(taskId, A2aTaskState.Working, null, final: false);
            record.Agent.Followup(prompt);
            await record.Agent.WhenIdle();
            FinalizeRun(record, taskId, startSeq);
        }
        catch (Exception error)
        {
            _tracker.FailTask(taskId, $"agent run failed: {error.Message}");
        }
        finally
        {
            record.ActiveTaskId = null;
            record.Gate.Release();
        }
    }

    private void FinalizeRun(SessionRecord record, string taskId, long startSeq)
    {
        if (_tracker.IsTerminalTask(taskId))
            return;
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
        var contextId = record.Agent.Id.Value;
        if (reason is TurnEndReason.Interrupted or TurnEndReason.Aborted)
        {
            _tracker.SetStatus(taskId, A2aTaskState.Canceled, AgentText("canceled", contextId, taskId), final: true);
            return;
        }
        if (reason is not TurnEndReason.Completed and not TurnEndReason.MaxTokens)
        {
            _tracker.FailTask(taskId, $"turn ended without completion ({reason?.ToString() ?? "no turn end"})");
            return;
        }
        var finalText = text.ToString();
        A2aMessage? agentMessage = finalText.Length > 0 ? AgentText(finalText, contextId, taskId) : null;
        if (agentMessage is not null)
            _tracker.AddArtifact(taskId, new A2aArtifact(FinalMessageArtifactId, agentMessage.Parts, "final message"), lastChunk: true);
        _tracker.CompleteTask(taskId, agentMessage);
    }

    private void OnSessionEvent(SessionEventNotification notification)
    {
        SessionRecord? record;
        lock (_gate)
            _sessions.TryGetValue(notification.Session.Id.Value, out record);
        if (record?.ActiveTaskId is not { } taskId)
            return;
        Translate(taskId, record.Agent.Id.Value, notification.Event);
    }

    private void Translate(string taskId, string contextId, SessionEvent sessionEvent)
    {
        switch (sessionEvent.Data)
        {
            case AssistantChunkPayload { Chunk: StreamChunk.TextDelta { Text.Length: > 0 } delta }:
                _tracker.SetStatus(taskId, A2aTaskState.Working,
                    new A2aMessage($"a2a-{taskId}-reply", A2aRoles.Agent, [new A2aTextPart(delta.Text)], contextId, taskId),
                    final: false);
                break;
            case ToolCallPayload toolCall:
                _tracker.SetStatus(taskId, A2aTaskState.Working,
                    AgentData(contextId, taskId, new JsonObject
                    {
                        ["type"] = "tool-call",
                        ["callId"] = toolCall.CallId.Value,
                        ["name"] = toolCall.Name,
                        ["arguments"] = toolCall.Arguments,
                    }),
                    final: false);
                break;
            case ToolResultPayload toolResult:
                _tracker.SetStatus(taskId, A2aTaskState.Working,
                    AgentData(contextId, taskId, new JsonObject
                    {
                        ["type"] = "tool-result",
                        ["callId"] = toolResult.Message.ToolSource.CallId.Value,
                        ["isError"] = toolResult.Message.Block.IsError == true,
                    }),
                    final: false);
                break;
        }
    }

    private void HookSessionEvents(SessionRecord record)
    {
        var agentCtx = record.Agent.Ctx;
        record.Removers.Add(agentCtx.OnWaterfall<UserQuestionsRequestNotification>(async (notification, next) =>
        {
            if (record.ActiveTaskId is not { } taskId)
                return await next();
            var answer = await _tracker.BeginInteraction(taskId, DescribeQuestions(notification.Request.Questions));
            return MapAnswer(notification.Request.Questions, answer);
        }));
        record.Removers.Add(agentCtx.OnWaterfall<ApprovalRequestNotification>(async (notification, next) =>
        {
            if (record.ActiveTaskId is not { } taskId)
                return await next();
            var answer = await _tracker.BeginInteraction(taskId, DescribeApproval(notification.Request));
            return IsAffirmative(answer) ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected;
        }));
        record.Removers.Add(agentCtx.On<SubagentStartNotification>(notification =>
        {
            if (record.ActiveTaskId is { } taskId)
            {
                _tracker.SetStatus(taskId, A2aTaskState.Working,
                    AgentText($"subagent started: {notification.Info.Provider}/{notification.Info.Id.Value}",
                        record.Agent.Id.Value, taskId),
                    final: false);
            }
        }));
        record.Removers.Add(agentCtx.On<SubagentEndNotification>(notification =>
        {
            if (record.ActiveTaskId is { } taskId)
            {
                _tracker.SetStatus(taskId, A2aTaskState.Working,
                    AgentText($"subagent ended: {notification.Info.Provider}/{notification.Info.Id.Value} ({SubagentStopReasonWire.Of(notification.Info.StopReason)})",
                        record.Agent.Id.Value, taskId),
                    final: false);
            }
        }));
    }

    private static A2aMessage DescribeQuestions(IReadOnlyList<AskUserQuestionItem> questions)
    {
        var builder = new StringBuilder();
        foreach (var question in questions)
        {
            builder.Append(question.Question);
            if (question.Options is { Count: > 0 } options)
                builder.Append($" (options: {string.Join(" | ", options.Select(option => option.Label))})");
            builder.Append('\n');
        }
        return new A2aMessage(Guid.NewGuid().ToString("N"), A2aRoles.Agent, [new A2aTextPart(builder.ToString().TrimEnd())]);
    }

    private static A2aMessage DescribeApproval(ApprovalRequest request)
    {
        var reason = request.Reason is { Length: > 0 } text ? $": {text}" : "";
        return new A2aMessage(Guid.NewGuid().ToString("N"), A2aRoles.Agent,
            [new A2aTextPart($"approval requested for tool \"{request.ToolName}\"{reason}. Reply yes to approve, anything else to reject.")]);
    }

    private static AskUserQuestionAnswer MapAnswer(IReadOnlyList<AskUserQuestionItem> questions, string answerText)
    {
        var trimmed = answerText.Trim();
        var answers = questions.Select(question =>
        {
            var matched = question.Options?.FirstOrDefault(option =>
                string.Equals(option.Label, trimmed, StringComparison.OrdinalIgnoreCase));
            return matched is not null
                ? new AskUserQuestionAnswerItem(question.Id, [matched.Label])
                : new AskUserQuestionAnswerItem(question.Id, [], trimmed);
        }).ToList();
        return new AskUserQuestionAnswer(answers);
    }

    private static bool IsAffirmative(string answer) => AffirmativeAnswers.Contains(answer.Trim().ToLowerInvariant());

    private static A2aMessage AgentText(string text, string? contextId = null, string? taskId = null)
        => new(Guid.NewGuid().ToString("N"), A2aRoles.Agent, [new A2aTextPart(text)], contextId, taskId);

    private static A2aMessage AgentData(string contextId, string taskId, JsonObject data)
        => new(Guid.NewGuid().ToString("N"), A2aRoles.Agent, [new A2aDataPart(A2aWire.DataElement(data))], contextId, taskId);
}
