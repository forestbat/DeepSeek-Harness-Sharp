using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Pty;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Host;

/**
 * 远端宿主能力后端: 直连进程内 harness 服务(会话 / 事件 / 工具 / 审批 / 文件 / PTY)。
 * 事件与审批是「远端发起」: 事件经 ctx 通知转成推送流; 审批经 ctx 的 waterfall 桥接成"远端问、本地答"。
 */
public sealed class HarnessRemoteHostBackend : IRemoteHostBackend, IDisposable
{
    private readonly HarnessApp _app;
    private readonly Channel<RemoteApprovalRequest> _approvals = Channel.CreateUnbounded<RemoteApprovalRequest>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly Channel<RemoteQuestionRequest> _questions = Channel.CreateUnbounded<RemoteQuestionRequest>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _pendingQuestions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentHandle> _handles = new(StringComparer.Ordinal);
    private readonly Func<bool> _approvalBridge;
    private readonly Func<bool> _questionBridge;
    private long _approvalSequence;
    private long _questionSequence;

    public HarnessRemoteHostBackend(HarnessApp app)
    {
        _app = app;
        _approvalBridge = app.Ctx.OnWaterfall<ApprovalRequestNotification>(BridgeApprovalAsync, new EventOptions { Global = true });
        _questionBridge = app.Ctx.OnWaterfall<UserQuestionsRequestNotification>(BridgeQuestionAsync, new EventOptions { Global = true });
    }

    private async ValueTask<object?> BridgeApprovalAsync(ApprovalRequestNotification notification, Func<ValueTask<object?>> next)
    {
        var requestId = Interlocked.Increment(ref _approvalSequence).ToString();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[requestId] = completion;
        var request = notification.Request;
        await _approvals.Writer.WriteAsync(new RemoteApprovalRequest(
            requestId,
            request.Agent.Id.Value,
            request.ToolName,
            request.Arguments,
            request.Reason));
        return await completion.Task.ConfigureAwait(false);
    }

    private async ValueTask<object?> BridgeQuestionAsync(UserQuestionsRequestNotification notification, Func<ValueTask<object?>> next)
    {
        var requestId = Interlocked.Increment(ref _questionSequence).ToString();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingQuestions[requestId] = completion;
        var request = notification.Request;
        var items = request.Questions
            .Select(question => new RemoteQuestionItem(
                question.Id,
                question.Question,
                question.Detail,
                question.Header,
                [.. (question.Options ?? []).Select(option => new RemoteQuestionOption(option.Label, option.Description))],
                question.MultiSelect,
                question.Intent?.Approve))
            .ToList();
        await _questions.Writer.WriteAsync(new RemoteQuestionRequest(requestId, request.Agent?.Id.Value ?? "", items));
        return await completion.Task.ConfigureAwait(false);
    }

    private AgentRegistry Registry => _app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
        ?? throw new InvalidOperationException("AgentRegistry 未注册");

    private ISessionPersistence? Persistence => _app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, strict: false);

    public Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
        => Task.FromResult(new HostInfo(HostProtocol.Version, "host", Platform(), _app.Home.Root));

    public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, RemoteSessionInfo>(StringComparer.Ordinal);
        foreach (var snapshot in Persistence?.List() ?? [])
        {
            if (snapshot.Header.IsSubagent)
                continue;
            byId[snapshot.Header.Id.Value] = Describe(snapshot.Header);
        }
        foreach (var agent in Registry.List())
        {
            if (agent.Session.Header.IsSubagent)
                continue;
            byId[agent.Id.Value] = Describe(agent);
        }
        return Task.FromResult<IReadOnlyList<RemoteSessionInfo>>([.. byId.Values.OrderByDescending(info => info.UpdatedAt)]);
    }

    public async Task<RemoteSessionInfo> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
    {
        // 会话 cwd 必须是绝对路径(POSIX 与 Windows 皆然): 空/相对一律解析到远端家目录。
        var resolved = string.IsNullOrWhiteSpace(cwd) ? ExpandHome("~") : ExpandHome(cwd);
        var handle = await Registry.Create(new CreateAgentOptions(Cwd: resolved), cancellationToken).ConfigureAwait(false);
        _handles[handle.Agent.Id.Value] = handle;
        return Describe(handle.Agent);
    }

    public async Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        if (Registry.Get(id) is { } live)
            return Describe(live);
        var handle = await Registry.Resume(new ResumeAgentOptions(id), cancellationToken).ConfigureAwait(false);
        _handles[handle.Agent.Id.Value] = handle;
        return Describe(handle.Agent);
    }

    public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
    {
        var content = new List<ContentBlock>();
        if (text.Length > 0)
            content.Add(new TextBlock(text));
        if (images.Count > 0 && _app.Ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName, false) is { } store)
        {
            foreach (var image in images)
                content.Add(new ImageBlock(store.Put(Convert.FromBase64String(image.Base64), image.MediaType, image.Width, image.Height, image.Name)));
        }
        Require(sessionId).Followup(MessageFactory.CreateUserMessage(content));
        return Task.CompletedTask;
    }

    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken)
    {
        Require(sessionId).Cancel(new AgentCancelCause.User());
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var runtime = _app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (runtime is null)
            return Task.FromResult<IReadOnlyList<string>>([]);
        var scope = Registry.Get(SessionId.Create(sessionId))?.ScopeKey;
        IReadOnlyList<string> names = [.. runtime.Schemas(scope).Select(schema => schema.Name)];
        return Task.FromResult(names);
    }

    public Task RespondApprovalAsync(string requestId, bool allow, string? reason, CancellationToken cancellationToken)
    {
        if (_pendingApprovals.TryRemove(requestId, out var completion))
            completion.TrySetResult(allow ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected);
        return Task.CompletedTask;
    }

    public Task RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken)
    {
        if (_pendingQuestions.TryRemove(requestId, out var completion))
            completion.TrySetResult(new AskUserQuestionAnswer(
                [.. answers.Select(answer => new AskUserQuestionAnswerItem(answer.Id, answer.Selected, answer.Custom))]));
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
        => File.ReadAllBytesAsync(ExpandHome(path), cancellationToken);

    public Task WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken)
        => File.WriteAllBytesAsync(ExpandHome(path), content, cancellationToken);

    public Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var directory = ExpandHome(path);
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
            throw new DirectoryNotFoundException($"远端目录不存在: {info.FullName}");
        var entries = new List<RemoteDirectoryEntry>();
        foreach (var child in info.EnumerateDirectories().OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            entries.Add(new RemoteDirectoryEntry(child.Name, child.FullName, true));
        foreach (var child in info.EnumerateFiles().OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            entries.Add(new RemoteDirectoryEntry(child.Name, child.FullName, false));
        return Task.FromResult(new RemoteDirectoryListing(info.FullName, info.Parent?.FullName, entries));
    }

    /** 远端用户的 `~`/空 路径折算为家目录: 用户填 `~/dsh-deploy` 时也能浏览。 */
    private static string ExpandHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == "~")
            return home;
        var trimmed = path.Trim();
        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(home, trimmed[2..]);
        return Path.GetFullPath(trimmed);
    }

    public async Task<string> StartPtyAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var session = await PtyHost.Default
            .StartAsync(new PtyStartInfo { FileName = fileName, Arguments = arguments, Home = _app.Home.Root }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return session.Id.Value;
    }

    public async Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken)
    {
        if (PtyHost.Default.Get(ptyId) is { } session)
            await session.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken)
        => PtyHost.Default.StopAsync(ptyId);

    public async IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        var live = Registry.Get(id);
        var buffer = Channel.CreateUnbounded<RemoteEventInfo>();
        Func<bool>? unsubscribe = null;
        long liveFrom = -1;
        if (live is not null)
        {
            // 先订阅再补历史, 避免两者之间新追加的事件被漏掉; 历史只发到订阅时刻为止。
            unsubscribe = _app.Ctx.On<SessionEventNotification>(notification =>
            {
                if (string.Equals(notification.Session.Id.Value, sessionId, StringComparison.Ordinal))
                    buffer.Writer.TryWrite(ToInfo(sessionId, notification.Event));
            });
            liveFrom = live.Session.Seq;
        }
        try
        {
            if (live is not null)
            {
                foreach (var sessionEvent in live.Session.SnapshotEvents(fromSeq, liveFrom))
                    yield return ToInfo(sessionId, sessionEvent);
            }
            else
            {
                foreach (var sessionEvent in ReadPersisted(id, fromSeq))
                    yield return ToInfo(sessionId, sessionEvent);
                yield break;
            }

            await foreach (var item in buffer.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            unsubscribe?.Invoke();
            buffer.Writer.TryComplete();
        }
    }

    public async IAsyncEnumerable<RemoteApprovalRequest> ApprovalsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _approvals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public async IAsyncEnumerable<RemoteQuestionRequest> QuestionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _questions.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public async IAsyncEnumerable<RemotePtyOutput> PtyOutputAsync(string ptyId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session = PtyHost.Default.Get(ptyId);
        if (session is null)
            yield break;
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await session.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                yield return new RemotePtyOutput(ptyId, "", true);
                yield break;
            }

            yield return new RemotePtyOutput(ptyId, Convert.ToBase64String(buffer, 0, read), false);
        }
    }

    private IAgent Require(string sessionId)
        => Registry.Get(SessionId.Create(sessionId)) ?? throw new InvalidOperationException($"unknown session: {sessionId}");

    private static RemoteSessionInfo Describe(IAgent agent)
    {
        var header = agent.Session.Header;
        return new RemoteSessionInfo(
            header.Id.Value,
            header.Cwd ?? "",
            agent.Status.ToString(),
            header.CreatedAt,
            header.Title,
            Summarize(agent.Session),
            LastEventTime(agent.Session));
    }

    private static RemoteSessionInfo Describe(SessionHeader header)
        => new(header.Id.Value, header.Cwd ?? "", "Idle", header.CreatedAt, header.Title, null, header.CreatedAt);

    /** 会话摘要: 最后一条用户文本, 供侧栏列表显示。 */
    private static string? Summarize(Session session)
    {
        var events = session.SnapshotEvents();
        for (var index = events.Count - 1; index >= 0; index--)
        {
            if (events[index].Data is not UserMessagePayload { Message.Source: UserMessageSource } user)
                continue;
            var text = string.Concat(user.Message.Content.OfType<TextBlock>().Select(block => block.Text));
            if (text.Length > 0)
                return text.Length > 120 ? text[..120] : text;
        }
        return null;
    }

    private static long LastEventTime(Session session)
    {
        var events = session.SnapshotEvents();
        return events.Count == 0 ? session.Header.CreatedAt : events[^1].Time;
    }

    private static RemoteEventInfo ToInfo(string sessionId, SessionEvent sessionEvent)
        => new(sessionId, DshJson.Serialize(sessionEvent));

    private IReadOnlyList<SessionEvent> ReadPersisted(SessionId id, long fromSeq)
    {
        using var handle = Persistence?.Open(id, SessionAccess.Read);
        return handle is null ? [] : handle.Read(fromSeq);
    }

    private static string Platform()
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public void Dispose()
    {
        _approvalBridge();
        _questionBridge();
        foreach (var handle in _handles.Values)
            handle.Dispose.Dispose();
        _handles.Clear();
        _approvals.Writer.TryComplete();
        _questions.Writer.TryComplete();
    }
}
