using System.Threading.Channels;

namespace Dsh.A2A;

/** taskId ↔ (SessionId, run) 映射与状态机；事件以序列化帧存档，供 SSE 续播。 */
public sealed class A2aTaskTracker
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    private sealed class TrackedTask
    {
        public required string Id { get; init; }
        public required string ContextId { get; init; }
        public A2aTaskState State { get; set; } = A2aTaskState.Submitted;
        public A2aMessage? StatusMessage { get; set; }
        public List<A2aMessage> History { get; } = [];
        public List<A2aArtifact> Artifacts { get; } = [];
        public List<string> Events { get; } = [];
        public List<Channel<string>> Subscribers { get; } = [];
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource<string>? PendingAnswer { get; set; }
        public CancellationTokenSource Cancel { get; } = new();
    }

    private readonly Dictionary<string, TrackedTask> _tasks = [];
    private readonly Lock _gate = new();

    public static bool IsTerminal(A2aTaskState state)
        => state is A2aTaskState.Completed or A2aTaskState.Failed or A2aTaskState.Canceled or A2aTaskState.Rejected;

    public string CreateTask(string contextId, A2aMessage userMessage)
    {
        lock (_gate)
        {
            var task = new TrackedTask { Id = Guid.NewGuid().ToString("N"), ContextId = contextId };
            task.History.Add(userMessage);
            _tasks[task.Id] = task;
            return task.Id;
        }
    }

    public bool IsTerminalTask(string taskId)
    {
        lock (_gate)
            return _tasks.TryGetValue(taskId, out var task) && IsTerminal(task.State);
    }

    public CancellationToken CancelTokenOf(string taskId)
    {
        lock (_gate)
        {
            return _tasks.TryGetValue(taskId, out var task)
                ? task.Cancel.Token
                : throw new A2aException(A2aErrorCodes.TaskNotFound, $"unknown task: {taskId}");
        }
    }

    public A2aTask Require(string taskId, int? historyLength = null)
    {
        lock (_gate)
        {
            return _tasks.TryGetValue(taskId, out var task)
                ? Snapshot(task, historyLength)
                : throw new A2aException(A2aErrorCodes.TaskNotFound, $"unknown task: {taskId}");
        }
    }

    public void SetStatus(string taskId, A2aTaskState state, A2aMessage? message, bool final)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task) || IsTerminal(task.State))
                return;
            task.State = state;
            task.StatusMessage = message;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            var frame = A2aWire.StatusEventJson(new A2aTaskStatusUpdateEvent(
                taskId, task.ContextId, new A2aTaskStatus(state, message, A2aWire.Now()), final));
            task.Events.Add(frame);
            Broadcast(task, frame, final);
        }
    }

    public void CompleteTask(string taskId, A2aMessage? agentMessage)
    {
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out var task) && agentMessage is not null && !IsTerminal(task.State))
                task.History.Add(agentMessage);
        }
        SetStatus(taskId, A2aTaskState.Completed, agentMessage, final: true);
    }

    public void FailTask(string taskId, string error)
    {
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out var task))
            {
                task.PendingAnswer?.TrySetCanceled();
                task.PendingAnswer = null;
            }
        }
        var message = new A2aMessage(Guid.NewGuid().ToString("N"), A2aRoles.Agent, [new A2aTextPart(error)]);
        SetStatus(taskId, A2aTaskState.Failed, message, final: true);
    }

    public void AddArtifact(string taskId, A2aArtifact artifact, bool lastChunk)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task) || IsTerminal(task.State))
                return;
            task.Artifacts.Add(artifact);
            task.UpdatedAt = DateTimeOffset.UtcNow;
            var frame = A2aWire.ArtifactEventJson(new A2aTaskArtifactUpdateEvent(
                taskId, task.ContextId, artifact, Append: false, LastChunk: lastChunk));
            task.Events.Add(frame);
            Broadcast(task, frame, final: false);
        }
    }

    public A2aStreamHandle SubscribeStream(string taskId)
    {
        var (backlog, live) = Subscribe(taskId);
        var frames = new List<string> { A2aWire.TaskJson(Require(taskId)) };
        frames.AddRange(backlog);
        return new A2aStreamHandle(taskId, frames, live);
    }

    private (IReadOnlyList<string> Backlog, ChannelReader<string>? Live) Subscribe(string taskId)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                throw new A2aException(A2aErrorCodes.TaskNotFound, $"unknown task: {taskId}");
            IReadOnlyList<string> backlog = [.. task.Events];
            if (IsTerminal(task.State))
                return (backlog, null);
            var channel = Channel.CreateUnbounded<string>();
            task.Subscribers.Add(channel);
            return (backlog, channel.Reader);
        }
    }

    public Task<string> BeginInteraction(string taskId, A2aMessage prompt)
    {
        TaskCompletionSource<string> pending;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task) || IsTerminal(task.State))
                throw new A2aException(A2aErrorCodes.UnsupportedOperation, $"task {taskId} cannot accept an interaction answer");
            pending = task.PendingAnswer ??= new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        SetStatus(taskId, A2aTaskState.InputRequired, prompt, final: false);
        return pending.Task;
    }

    /** 客户端下一次 send 的 message 即回答：按会话找到挂起的交互并唤醒它。 */
    public string? TryAnswer(string contextId, string answerText)
    {
        lock (_gate)
        {
            var task = _tasks.Values.FirstOrDefault(candidate =>
                candidate.ContextId == contextId && candidate.PendingAnswer is not null && !IsTerminal(candidate.State));
            if (task?.PendingAnswer is not { } pending)
                return null;
            task.PendingAnswer = null;
            pending.TrySetResult(answerText);
            var receipt = new A2aMessage(Guid.NewGuid().ToString("N"), A2aRoles.Agent,
                [new A2aTextPart("answer received")], contextId, task.Id);
            task.State = A2aTaskState.Working;
            task.StatusMessage = receipt;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            var frame = A2aWire.StatusEventJson(new A2aTaskStatusUpdateEvent(
                task.Id, contextId, new A2aTaskStatus(A2aTaskState.Working, receipt, A2aWire.Now()), false));
            task.Events.Add(frame);
            Broadcast(task, frame, final: false);
            return task.Id;
        }
    }

    public void RequestCancel(string taskId)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                throw new A2aException(A2aErrorCodes.TaskNotFound, $"unknown task: {taskId}");
            if (IsTerminal(task.State))
                throw new A2aException(A2aErrorCodes.TaskNotCancelable, $"task {taskId} is already in a terminal state");
            task.PendingAnswer?.TrySetCanceled();
            task.Cancel.Cancel();
        }
        var message = new A2aMessage(Guid.NewGuid().ToString("N"), A2aRoles.Agent, [new A2aTextPart("canceled by client")]);
        SetStatus(taskId, A2aTaskState.Canceled, message, final: true);
    }

    public A2aListTasksResult List(A2aListTasksParams parameters)
    {
        lock (_gate)
        {
            var pageSize = Math.Clamp(parameters.PageSize ?? DefaultPageSize, 1, MaxPageSize);
            var offset = ParsePageToken(parameters.PageToken);
            var query = _tasks.Values
                .Where(task => parameters.ContextId is null || task.ContextId == parameters.ContextId)
                .Where(task => parameters.Status is null || task.State == parameters.Status)
                .OrderByDescending(task => task.UpdatedAt)
                .ToList();
            var page = query.Skip(offset).Take(pageSize).Select(task => Snapshot(task, null)).ToList();
            var next = offset + pageSize < query.Count ? (offset + pageSize).ToString() : "";
            return new A2aListTasksResult(page, next, pageSize, query.Count);
        }
    }

    private static int ParsePageToken(string? pageToken)
    {
        if (string.IsNullOrEmpty(pageToken))
            return 0;
        return int.TryParse(pageToken, out var offset) && offset >= 0
            ? offset
            : throw new A2aException(A2aErrorCodes.InvalidParams, $"invalid pageToken: {pageToken}");
    }

    private static A2aTask Snapshot(TrackedTask task, int? historyLength)
    {
        var history = historyLength is { } length ? task.History.TakeLast(length).ToList() : [.. task.History];
        return new A2aTask(
            task.Id,
            task.ContextId,
            new A2aTaskStatus(task.State, task.StatusMessage, task.UpdatedAt.ToString("o")),
            task.Artifacts.Count > 0 ? [.. task.Artifacts] : null,
            history.Count > 0 ? history : null);
    }

    private static void Broadcast(TrackedTask task, string frame, bool final)
    {
        foreach (var subscriber in task.Subscribers)
        {
            subscriber.Writer.TryWrite(frame);
            if (final)
                subscriber.Writer.TryComplete();
        }
        if (final)
            task.Subscribers.Clear();
    }
}
