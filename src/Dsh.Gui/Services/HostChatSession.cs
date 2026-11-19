using Avalonia.Threading;
using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/** A GUI projection of a Host-owned chat. Disposing detaches the view, never stops the agent. */
public sealed class HostChatSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _subscription;
    private readonly List<SessionEvent> _events = [];
    private long _generation;
    private long _navigationGeneration;
    private long _reloadGeneration;
    private IWorkspaceMachine? _followingMachine;
    private string _machine = MachineSpec.LocalName;
    private string _kind = HostSessionKinds.Harness;

    private HostChatSession(
        FleetWorkspace fleet,
        string machine,
        string kind,
        RemoteSessionSummary info,
        RemoteSessionSnapshot snapshot)
    {
        Fleet = fleet;
        _machine = machine;
        _kind = kind;
        Info = info;
        Header = snapshot.Header;
        ReplaceEvents(snapshot);
    }

    public FleetWorkspace Fleet { get; }
    public RemoteSessionSummary Info { get; private set; }
    public SessionHeader Header { get; private set; }
    public SessionId Id => Header.Id;
    public string Machine => _machine;
    public string Kind => _kind;
    public bool IsHarness => string.Equals(_kind, HostSessionKinds.Harness, StringComparison.Ordinal);
    public IReadOnlyList<SessionEvent> Events => _events;
    public IReadOnlyList<RemoteCommandDescriptor> Commands { get; private set; } = [];
    public event Action? Reset;
    public event Action<IReadOnlyList<SessionEvent>>? Received;
    /** 会话被就地截断(撤回): 参数是保留下来的事件条数。视图据此按序号增量截断, 不必整屏重建。 */
    public event Action<long>? Truncated;
    public event Action<string>? Failed;

    public static async Task<HostChatSession> OpenAsync(FleetWorkspace fleet, string cwd, string? sessionId, CancellationToken cancellationToken = default)
    {
        fleet.Start();
        var lease = await fleet.Find(MachineSpec.LocalName)!.WaitConnectedAsync(cancellationToken);
        if (!lease.Client.Supports(SessionEditingProtocol.Capability)
            || !lease.Client.Supports(PluginsProtocol.Capability))
            throw new InvalidOperationException("本机 Host 版本不支持完整 Chat；请升级 dsharp 并重启 Host");
        var info = sessionId is null
            ? await OpenRecentOrCreateAsync(lease.Client, cwd, cancellationToken)
            : await lease.Client.ResumeSessionAsync(sessionId, cancellationToken);
        var snapshot = await LoadSnapshotAsync(lease.Client, info.Id, cancellationToken);
        var chat = new HostChatSession(fleet, MachineSpec.LocalName, HostSessionKinds.Harness, info, snapshot);
        chat.Commands = await lease.Client.SessionEditing.ListSessionCommandsAsync(info.Id, cancellationToken);
        return chat;
    }

    /**
     * 打开时带哪些历史。
     *
     * 一次快照只回**一页**(受 HostProtocol.SnapshotPageBytes 限制), 所以过去按 TotalEventCount 续取到齐:
     * 一页一页把整段历史拖下来, 代价与历史长度成正比 —— 383MB / 1,043,710 条会话实测 24 分钟仍未完成、
     * 堆 1.3GB, 而这只是为了"打开会话接着往下聊"。所以超大会话只取尾部一窗(更早的历史留给按需取页),
     * 中小会话照旧整段取齐: 那里整段也就几页, 少了反而让人看不到刚聊完的内容。
     */
    private static async Task<RemoteSessionSnapshot> LoadSnapshotAsync(
        IWorkspaceHost client,
        string id,
        CancellationToken cancellationToken)
    {
        if (client is not RemoteHostClient paged)
            return await client.GetSessionSnapshotAsync(id, cancellationToken).ConfigureAwait(false);
        var total = await paged.TotalEventCountAsync(id, cancellationToken).ConfigureAwait(false);
        if (total > WholeSnapshotEventLimit)
            return await paged.GetSessionSnapshotAsync(id, OpenTailEvents, cancellationToken).ConfigureAwait(false);
        return await LoadWholeHistoryAsync(paged, id, cancellationToken).ConfigureAwait(false);
    }

    /** 打开时整段带走的历史上限: 再多就是几十页、分钟级的打开。 */
    private const int WholeSnapshotEventLimit = 200_000;

    /** 超大会话打开时只带尾部这么多条: 几十个回合, 与 Host 恢复时的尾窗同量级。 */
    private const int OpenTailEvents = 20_000;

    /** 按 TotalEventCount 续取到齐(中小会话的路径)。 */
    private static async Task<RemoteSessionSnapshot> LoadWholeHistoryAsync(
        RemoteHostClient client,
        string id,
        CancellationToken cancellationToken)
    {
        var first = await client.GetSessionSnapshotAsync(id, cancellationToken).ConfigureAwait(false);
        if (first.Events.Count >= first.TotalEventCount)
            return first;
        var events = new List<SessionEvent>(first.Events);
        var from = NextSeqOf(first.Events);
        while (events.Count < first.TotalEventCount)
        {
            var page = await client.GetSessionSnapshotFromAsync(id, from, cancellationToken).ConfigureAwait(false);
            var next = NextSeqOf(page.Events);
            if (next <= from)
                break;
            events.AddRange(page.Events);
            from = next;
        }
        return first with { Events = events };
    }

    /** 下一条事件的序号。序号是全局的, 而事件列表可能不是从 0 开始(窗口化/分页快照), 所以条数不能当序号用。 */
    private static long NextSeqOf(IReadOnlyList<SessionEvent> events)
        => events.Count == 0 ? 0 : events[^1].Seq + 1;

    /**
     * 未显式指定会话时恢复同工作区最近一次会话, 没有可恢复的才新建。
     * 只看目录不看 Summary: 磁盘上的会话由 Host 用 SessionHeader 投影, Summary 恒为 null,
     * 只有活着且已有用户消息的会话才有摘要 —— 拿摘要当筛选条件, Host 一重启就永远选不中,
     * 于是每次启动都新建会话(空会话又因为没事件而不在列表里, 下次启动照样新建)。
     */
    private static async Task<RemoteSessionSummary> OpenRecentOrCreateAsync(
        IWorkspaceHost client,
        string cwd,
        CancellationToken cancellationToken)
    {
        var recent = (await client.ListSessionsAsync(cancellationToken))
            .Where(session => SameDirectory(session.Cwd, cwd))
            .OrderByDescending(session => session.UpdatedAt)
            .FirstOrDefault();
        if (recent is not null)
            return await client.ResumeSessionAsync(recent.SessionId, cancellationToken);
        // 还没写过事件的新会话不落盘也不进 ListSessionsAsync, 但它仍是 Host 里活着的那一个: 复用它, 不再新建。
        var pending = (await client.ListHostSessionsAsync(cancellationToken))
            .Where(session => session.Kind == HostSessionKinds.Harness
                && session.Status != HostSessionStatuses.Exited
                && SameDirectory(session.Cwd, cwd))
            .OrderByDescending(session => session.UpdatedAt)
            .FirstOrDefault();
        return pending is not null
            ? await client.ResumeSessionAsync(pending.SessionId, cancellationToken)
            : await client.CreateSessionAsync(cwd, cancellationToken);
    }

    private static bool SameDirectory(string? path, string cwd)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(cwd),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Follow()
    {
        _subscription?.Cancel();
        _subscription = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation = ++_generation;
        _followingMachine = Fleet.Find(_machine)!;
        _ = FollowAsync(_followingMachine, Info.Id, generation, _subscription.Token);
    }

    public void RefreshConnection()
    {
        if (!_lifetime.IsCancellationRequested && !ReferenceEquals(_followingMachine, Fleet.Find(_machine)))
            Follow();
    }

    /** 订阅起点: 本地已有的下一条事件。用序号而不是条数: 窗口化/分页快照的列表不从 0 开始。 */
    private long NextSeq => NextSeqOf(_events);

    private async Task FollowAsync(IWorkspaceMachine machine, string id, long generation, CancellationToken cancellationToken)
    {
        var events = new DispatcherBridge<SessionEvent>(batch =>
        {
            if (generation != _generation || cancellationToken.IsCancellationRequested)
                return;
            var next = NextSeq;
            foreach (var sessionEvent in batch)
                if (sessionEvent.Seq >= next)
                    _events.Add(sessionEvent);
            Received?.Invoke(batch);
        });
        try
        {
            await foreach (var item in machine.SubscribeAsync(id, NextSeq, cancellationToken))
            {
                if (item.Reset)
                {
                    await ReloadAsync(cancellationToken);
                    continue;
                }
                if (item.TruncatedTo is { } keep)
                {
                    await TruncateProjectionAsync(keep, cancellationToken);
                    continue;
                }
                if (item.Event is { } sessionEvent)
                    events.Enqueue(sessionEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() => { if (generation == _generation && !_lifetime.IsCancellationRequested) Failed?.Invoke(error.Message); });
        }
    }

    public async Task<IWorkspaceHost> ClientAsync(CancellationToken cancellationToken = default)
        => (await Fleet.Find(_machine)!.WaitConnectedAsync(cancellationToken)).Client;

    /**
     * 会话在服务端被就地截断了: 本地事件列表按序号删掉尾部, 再把条数通知视图。
     * 视图据此增量截断消息, 不整屏重建。
     *
     * 之后补一次**非强制**的快照: 撤回后新写入的事件会复用同一个序号, 而它可能在截断通知之前
     * 就已经到达并被当成重复丢掉。非强制的 ReloadAsync 只把快照里多出来的尾部追加进来
     * (TryAppend), 前缀一致时不会重建界面, 正好把那条漏掉的事件补回来。
     */
    private async Task TruncateProjectionAsync(long keepEventCount, CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_lifetime.IsCancellationRequested)
                return;
            var keep = (int)Math.Clamp(keepEventCount, 0, _events.Count);
            if (keep < _events.Count)
                _events.RemoveRange(keep, _events.Count - keep);
            Truncated?.Invoke(keep);
        });
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SwitchAsync(string id, CancellationToken cancellationToken = default)
        => await SwitchToAsync(_machine, id, _kind, cancellationToken);

    public async Task SwitchToAsync(
        string machine,
        string id,
        string kind,
        CancellationToken cancellationToken = default)
    {
        var navigation = ++_navigationGeneration;
        var connection = Fleet.Find(machine) ?? throw new InvalidOperationException($"no machine named \"{machine}\"");
        var client = (await connection.WaitConnectedAsync(cancellationToken).ConfigureAwait(false)).Client;
        var info = kind == HostSessionKinds.Harness
            ? await client.ResumeSessionAsync(id, cancellationToken).ConfigureAwait(false)
            : ToInfo(await ResumeAgentAsync(connection, client, id, cancellationToken).ConfigureAwait(false));
        var snapshotTask = LoadSnapshotAsync(client, id, cancellationToken);
        var commandsTask = kind == HostSessionKinds.Harness
            ? client.SessionEditing.ListSessionCommandsAsync(id, cancellationToken)
            : Task.FromResult<IReadOnlyList<RemoteCommandDescriptor>>([]);
        await Task.WhenAll(snapshotTask, commandsTask).ConfigureAwait(false);
        var snapshot = await snapshotTask.ConfigureAwait(false);
        var commands = await commandsTask.ConfigureAwait(false);
        var header = snapshot.Header;
        var events = snapshot.Events;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (navigation != _navigationGeneration || _lifetime.IsCancellationRequested)
                return;
            _subscription?.Cancel();
            ++_generation;
            _machine = machine;
            _kind = kind;
            Info = info;
            Header = header;
            _events.Clear();
            _events.AddRange(events);
            Commands = commands;
            Reset?.Invoke();
            Follow();
        });
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
        => ReloadAsync(false, cancellationToken);

    public async Task ReloadAsync(bool forceReset, CancellationToken cancellationToken = default)
    {
        var id = Info.Id;
        var generation = _generation;
        var reload = ++_reloadGeneration;
        var snapshot = await LoadSnapshotAsync(await ClientAsync(cancellationToken).ConfigureAwait(false), id, cancellationToken).ConfigureAwait(false);
        var header = snapshot.Header;
        var replacement = snapshot.Events;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _generation || reload != _reloadGeneration || _lifetime.IsCancellationRequested)
                return;
            var titleChanged = !string.Equals(Header.Title, header.Title, StringComparison.Ordinal);
            Header = header;
            // 命令通常只在尾部追加事件(例如 /model、/reasoning、/preset)。全量 ReplaceEvents + Reset 会清空并重建
            // 整个消息/视觉树, 长会话每次选择都要付一遍 O(历史) 的布局与样式分配。
            if (forceReset || titleChanged || !TryAppend(replacement, out var appended))
            {
                ReplaceEvents(snapshot);
                Reset?.Invoke();
                return;
            }

            if (appended.Count == 0)
                return;
            _events.AddRange(appended);
            Received?.Invoke(appended);
        });
    }

    /** 快照只是旧事件列表的尾部追加时才返回 true; 截断/重写会话仍需整屏重建。 */
    private bool TryAppend(IReadOnlyList<SessionEvent> replacement, out List<SessionEvent> appended)
    {
        appended = [];
        if (replacement.Count < _events.Count)
            return false;
        for (var index = 0; index < _events.Count; index++)
        {
            if (replacement[index].Seq != _events[index].Seq)
                return false;
        }

        appended = [.. replacement.Skip(_events.Count)];
        return true;
    }

    private void ReplaceEvents(RemoteSessionSnapshot snapshot)
    {
        _events.Clear();
        _events.AddRange(snapshot.Events);
    }

    public async Task RefreshCommandsAsync()
    {
        if (!IsHarness)
        {
            Commands = [];
            return;
        }
        var id = Info.Id;
        var commands = await (await ClientAsync(_lifetime.Token)).SessionEditing.ListSessionCommandsAsync(id, _lifetime.Token);
        if (Info.Id == id && !_lifetime.IsCancellationRequested)
            Commands = commands;
    }

    public async Task<RemoteCommandResult> ExecuteAsync(string line)
    {
        if (!IsHarness)
            return new RemoteCommandResult(false, null, null);
        var result = await (await ClientAsync(_lifetime.Token)).SessionEditing.ExecuteSessionCommandAsync(Info.Id, line, _lifetime.Token);

        return result;
    }

    public async Task<string> RunAsync(string line)
    {
        var result = await ExecuteAsync(line);
        return result.Known ? result.Text ?? "" : $"未知命令: {line}";
    }

    public async Task SendAsync(string text, IReadOnlyList<RemoteImageBlock>? images = null)
    {
        var client = await ClientAsync(_lifetime.Token);
        if (!IsHarness)
        {
            await client.Agent.PromptAgentAsync(Info.Id, text, _lifetime.Token);
            return;
        }
        await client.ResumeSessionAsync(Info.Id, _lifetime.Token);
        await client.SendMessageAsync(Info.Id, text, images ?? [], _lifetime.Token);
    }

    private static async Task<RemoteSessionSummary> ResumeAgentAsync(
        IWorkspaceMachine connection,
        IWorkspaceHost client,
        string id,
        CancellationToken cancellationToken)
    {
        var current = connection.Sessions.FirstOrDefault(session => string.Equals(session.SessionId, id, StringComparison.Ordinal));
        if (current is { Status: not HostSessionStatuses.Exited })
            return current;
        return await client.Agent.ResumeAgentAsync(id, cancellationToken);
    }

    private static RemoteSessionSummary ToInfo(RemoteSessionSummary session)
        => new(
            session.SessionId,
            session.ProjectPath ?? "",
            session.Status,
            session.CreatedAt,
            session.Title,
            null,
            session.LastActivityAt);

    public void Dispose()
    {
        ++_generation;
        _lifetime.Cancel();
        _subscription?.Cancel();
    }
}


