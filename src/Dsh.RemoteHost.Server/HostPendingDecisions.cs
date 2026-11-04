namespace Dsh.RemoteHost;

/** 谁给出了结局: 客户端自报名(或连接 id)与连接 id; host 自己取消时没有连接。 */
public readonly record struct PendingResolver(string Name, string? ConnectionId)
{
    public static readonly PendingResolver Host = new("host", null);
}

/** A request that just started waiting for an answer; Summary is the tool of an approval or the first question's text. */
public sealed record HostPendingAsked(string Kind, string RequestId, string SessionId, string Summary);

/**
 * 审批与问答的唯一权威队列: 每次变更在同一锁序下发布完整 pending 投影, 先答者生效。
 * 新订阅也先收到完整投影; 结局仅随该次变更携带审计信息, 不另存重放缓存。
 */
public sealed class HostPendingDecisions : IDisposable
{
    private readonly Lock _gate = new();
    private readonly HostBroadcast _broadcast = new();
    private readonly OrderedDictionary<string, PendingEntry> _entries = new(StringComparer.Ordinal);

    public int SubscriberCount => _broadcast.SubscriberCount;

    /** 在锁外触发, 消费者重新查询当前数量, 不保存投影副本。 */
    public event Action<string>? CountChanged;

    /** Host 内部通知业务事件, 不作为另一条线协议。 */
    public event Action<HostPendingAsked>? Asked;

    private sealed record PendingEntry(
        string Kind,
        string SessionId,
        string? Scope,
        RemoteApprovalRequest? Approval,
        RemoteQuestionRequest? Question,
        TaskCompletionSource<object?> Completion);

    public Task<RemoteApprovalResponse?> AskApprovalAsync(RemoteApprovalRequest request, string? scope = null, CancellationToken cancellationToken = default)
        => AskAsync<RemoteApprovalResponse>(new PendingEntry(
            PendingKinds.Approval, request.SessionId, scope, request, null, NewCompletion()), request.RequestId, cancellationToken);

    public Task<RemoteQuestionResponse?> AskQuestionAsync(RemoteQuestionRequest request, string? scope = null, CancellationToken cancellationToken = default)
        => AskAsync<RemoteQuestionResponse>(new PendingEntry(
            PendingKinds.Question, request.SessionId, scope, null, request, NewCompletion()), request.RequestId, cancellationToken);

    public bool RespondApproval(RemoteApprovalResponse response, PendingResolver resolver)
        => Resolve(response.RequestId, PendingKinds.Approval, response,
            response.Cancelled ? PendingResolutions.Cancelled : response.Allow ? PendingResolutions.Allowed : PendingResolutions.Rejected, resolver);

    public bool RespondQuestion(RemoteQuestionResponse response, PendingResolver resolver)
    {
        if (response.Status == "dismissed")
            response = response with { Answers = [], Reason = "user_cancelled" };
        return Resolve(response.RequestId, PendingKinds.Question, response,
            response.Status == "dismissed" ? PendingResolutions.Dismissed : PendingResolutions.Answered, resolver);
    }

    public void CancelSession(string sessionId) => CancelWhere(entry => entry.SessionId == sessionId);

    public void CancelScope(string scope) => CancelWhere(entry => entry.Scope == scope);

    public RemotePendingList List()
    {
        lock (_gate)
            return Snapshot();
    }

    private RemotePendingList Snapshot() => new(
        [.. _entries.Values.Select(entry => entry.Approval).OfType<RemoteApprovalRequest>()],
        [.. _entries.Values.Select(entry => entry.Question).OfType<RemoteQuestionRequest>()]);

    public int CountFor(string sessionId)
    {
        lock (_gate)
            return _entries.Values.Count(entry => entry.SessionId == sessionId);
    }

    public HostSubscription Subscribe()
    {
        lock (_gate)
            return _broadcast.Subscribe([Change()]);
    }

    public void Dispose() => CancelWhere(_ => true);

    private static TaskCompletionSource<object?> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<T?> AskAsync<T>(PendingEntry entry, string requestId, CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_entries.TryAdd(requestId, entry))
                throw new InvalidOperationException($"pending request id already in use: {requestId}");
            _broadcast.Publish(Change());
        }
        CountChanged?.Invoke(entry.SessionId);
        Asked?.Invoke(new HostPendingAsked(entry.Kind, requestId, entry.SessionId, entry.Approval?.Tool ?? entry.Question?.Questions.FirstOrDefault()?.Question ?? ""));
        await using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state =>
            {
                var (owner, id, expected) = ((HostPendingDecisions, string, PendingEntry))state!;
                owner.Resolve(id, expected.Kind, null, PendingResolutions.Cancelled, PendingResolver.Host, expected);
            }, (this, requestId, entry))
            : default;
        return await entry.Completion.Task.ConfigureAwait(false) as T;
    }

    private bool Resolve(string requestId, string kind, object? answer, string resolution, PendingResolver resolver, PendingEntry? expected = null)
    {
        PendingEntry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(requestId, out entry) || entry.Kind != kind || expected is not null && !ReferenceEquals(entry, expected))
                return false;
            if (answer is RemoteApprovalResponse { Cancelled: false } decision && entry.Approval?.Availability is { } availability
                && !(decision.Allow ? decision.RememberForSession ? availability.AllowForSession : availability.AllowOnce : availability.Reject))
                return false;
            if (answer is RemoteQuestionResponse questionResponse
                && (entry.Question is not { } question || !IsValidQuestionResponse(question, questionResponse)))
                return false;
            _entries.Remove(requestId);
            _broadcast.Publish(Change(Resolved(requestId, entry, resolution, resolver)));
        }
        entry.Completion.TrySetResult(answer);
        CountChanged?.Invoke(entry.SessionId);
        return true;
    }

    private static bool IsValidQuestionResponse(RemoteQuestionRequest request, RemoteQuestionResponse response)
    {
        if (response.Status == "dismissed")
            return true;
        if (response.Status != "answered" || response.Answers is null || response.Answers.Count != request.Questions.Count)
            return false;
        for (var questionIndex = 0; questionIndex < request.Questions.Count; questionIndex++)
        {
            var question = request.Questions[questionIndex];
            RemoteQuestionAnswerItem? answer = null;
            for (var answerIndex = 0; answerIndex < response.Answers.Count; answerIndex++)
            {
                var candidate = response.Answers[answerIndex];
                if (candidate is null || candidate.Id != question.Id)
                    continue;
                if (answer is not null)
                    return false;
                answer = candidate;
            }
            if (answer is null || !IsValidQuestionAnswer(question, answer))
                return false;
        }
        return true;
    }

    private static bool IsValidQuestionAnswer(RemoteQuestionItem question, RemoteQuestionAnswerItem answer)
    {
        if (answer.Selected is null || !question.MultiSelect && answer.Selected.Count > 1
            || !question.AllowCustom && answer.Custom is not null)
            return false;
        if (answer.Selected.Count == 0 && (!question.AllowCustom || string.IsNullOrWhiteSpace(answer.Custom)))
            return false;
        for (var selectedIndex = 0; selectedIndex < answer.Selected.Count; selectedIndex++)
        {
            var value = answer.Selected[selectedIndex];
            var occurrences = 1;
            for (var previous = 0; previous < selectedIndex; previous++)
                if (answer.Selected[previous] == value)
                    occurrences++;
            var available = 0;
            for (var optionIndex = 0; optionIndex < question.Options.Count; optionIndex++)
            {
                var option = question.Options[optionIndex];
                if (!option.Disabled && (option.Value ?? option.Label) == value)
                    available++;
            }
            if (occurrences > available)
                return false;
        }
        return true;
    }

    private void CancelWhere(Func<PendingEntry, bool> match)
    {
        List<PendingEntry> cancelled;
        lock (_gate)
        {
            var ids = _entries.Where(pair => match(pair.Value)).Select(pair => pair.Key).ToList();
            cancelled = [.. ids.Select(id => _entries[id])];
            foreach (var id in ids)
            {
                var entry = _entries[id];
                _entries.Remove(id);
                _broadcast.Publish(Change(Resolved(id, entry, PendingResolutions.Cancelled, PendingResolver.Host)));
            }
        }
        foreach (var entry in cancelled)
            entry.Completion.TrySetResult(null);
        foreach (var sessionId in cancelled.Select(entry => entry.SessionId).Distinct(StringComparer.Ordinal))
            CountChanged?.Invoke(sessionId);
    }

    private HostNotification Change(RemotePendingResolved? resolution = null)
        => HostNotification.Create(HostProtocol.NotificationPendingChanged, new RemotePendingChange(Snapshot(), resolution), HostProtocolJsonContext.Default.RemotePendingChange);

    private static RemotePendingResolved Resolved(string requestId, PendingEntry entry, string resolution, PendingResolver resolver)
        => new(requestId, entry.Kind, entry.SessionId, resolution, resolver.Name, resolver.ConnectionId);
}
