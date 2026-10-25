using Avalonia.Threading;
using Dsh.Core;
using Dsh.Fleet;
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
    private MachineConnection? _followingMachine;
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
    public event Action<string>? Failed;

    public static async Task<HostChatSession> OpenAsync(FleetWorkspace fleet, string cwd, string? sessionId, CancellationToken cancellationToken = default)
    {
        fleet.Start();
        var lease = await fleet.Find(MachineSpec.LocalName)!.WaitConnectedAsync(cancellationToken);
        if (!lease.Client.Supports(SessionEditingProtocol.Capability)
            || !lease.Client.Supports(PluginsProtocol.Capability))
            throw new InvalidOperationException("本机 Host 版本不支持完整 Chat；请升级 dsharp 并重启 Host");
        var info = sessionId is null
            ? await lease.Client.CreateSessionAsync(cwd, cancellationToken)
            : await lease.Client.ResumeSessionAsync(sessionId, cancellationToken);
        var snapshot = await lease.Client.GetSessionSnapshotAsync(info.Id, cancellationToken);
        var chat = new HostChatSession(fleet, MachineSpec.LocalName, HostSessionKinds.Harness, info, snapshot);
        chat.Commands = await lease.Client.SessionEditing.ListSessionCommandsAsync(info.Id, cancellationToken);
        return chat;
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

    private async Task FollowAsync(MachineConnection machine, string id, long generation, CancellationToken cancellationToken)
    {
        var events = new DispatcherBridge<SessionEvent>(batch =>
        {
            if (generation != _generation || cancellationToken.IsCancellationRequested)
                return;
            foreach (var sessionEvent in batch)
                if (sessionEvent.Seq >= _events.Count)
                    _events.Add(sessionEvent);
            Received?.Invoke(batch);
        });
        try
        {
            await foreach (var item in machine.SubscribeAsync(id, _events.Count, cancellationToken))
            {
                if (item.Reset)
                {
                    await ReloadAsync(cancellationToken);
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

    public async Task<RemoteHostClient> ClientAsync(CancellationToken cancellationToken = default)
        => (await Fleet.Find(_machine)!.WaitConnectedAsync(cancellationToken)).Client;

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
        var snapshotTask = client.GetSessionSnapshotAsync(id, cancellationToken);
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

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var id = Info.Id;
        var generation = _generation;
        var reload = ++_reloadGeneration;
        var snapshot = await (await ClientAsync(cancellationToken).ConfigureAwait(false)).GetSessionSnapshotAsync(id, cancellationToken).ConfigureAwait(false);
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
            if (titleChanged || !TryAppend(replacement, out var appended))
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
        MachineConnection connection,
        RemoteHostClient client,
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




