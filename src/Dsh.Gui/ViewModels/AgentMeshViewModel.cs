using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.RemoteHost;
using Dsh.Transport;
using Dsh.Workspace;
using StreamJsonRpc;

namespace Dsh.Gui.ViewModels;

public sealed record DiscoveredAgentRowViewModel(
    DiscoveredAgentCard Card,
    AgentTrustRecord? TrustRecord)
{
    public string CardId => Card.CardId;
    public string Name => Card.Name;
    public string Description => Card.Description;
    public string Endpoint => Card.Endpoint;
    public AgentBackendKind BackendKind => Card.BackendKind;
    public AgentTrustStatus TrustStatus => TrustRecord?.Status ?? AgentTrustStatus.Discovered;
    public bool IsTrusted => TrustStatus == AgentTrustStatus.Trusted;
    public string TrustBadge => IsTrusted ? "已信任 (Trusted)" : $"未信任 ({TrustStatus})";
    public string CapabilitiesText => Card.Capabilities.Count > 0 ? string.Join(", ", Card.Capabilities) : "无";
}

/** Workbench view model for 0.5.0 persistent agents, rooms, discovery/trust and external channels. */
public sealed partial class AgentMeshViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceClient? _workspace;
    private readonly FleetWorkspace? _fleet;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Lock _watchGate = new();
    private IWorkspaceMesh? _directMesh;
    private IWorkspaceHost? _directHost;
    private IWorkspaceMesh? _watchedMesh;
    private CancellationTokenSource? _watchCancellation;
    private bool _disposed;

    public AgentMeshViewModel(FleetWorkspace fleet)
    {
        _fleet = fleet;
        _fleet.Changed += OnWorkspaceChanged;
        RefreshMachineNames();
    }

    public AgentMeshViewModel(IWorkspaceClient? workspace = null)
    {
        _workspace = workspace;
        if (_workspace is not null)
            _workspace.Changed += OnWorkspaceChanged;
        RefreshMachineNames();
    }

    private IWorkspaceClient? CurrentWorkspace => _fleet?.Client ?? _workspace;

    public ObservableCollection<string> MachineNames { get; } = [];
    public ObservableCollection<AgentProfile> Agents { get; } = [];
    public ObservableCollection<RemoteExecutionTarget> Targets { get; } = [];
    public ObservableCollection<CollaborationRoom> Rooms { get; } = [];
    public ObservableCollection<RoomMessage> RoomMessages { get; } = [];
    public ObservableCollection<DiscoveredAgentRowViewModel> DiscoveredAgents { get; } = [];
    public ObservableCollection<AgentTrustRecord> TrustRecords { get; } = [];
    public ObservableCollection<ChannelBinding> Channels { get; } = [];
    public ObservableCollection<AutomationTriggerRule> Triggers { get; } = [];

    public IReadOnlyList<AgentBackendKind> BackendKinds { get; } = Enum.GetValues<AgentBackendKind>();
    public IReadOnlyList<AgentMessageIntent> MessageIntents { get; } = Enum.GetValues<AgentMessageIntent>();
    public IReadOnlyList<RoomRole> InviteRoles { get; } = [RoomRole.Observer, RoomRole.Collaborator, RoomRole.Approver, RoomRole.Owner];

    [ObservableProperty] private string _selectedMachine = MachineSpec.LocalName;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "选择机器并刷新智能体网络。";

    [ObservableProperty] private AgentProfile? _selectedAgent;
    [ObservableProperty] private string _newAgentName = "";
    [ObservableProperty] private string _newAgentDescription = "";
    [ObservableProperty] private AgentBackendKind _newAgentBackendKind = AgentBackendKind.Local;
    [ObservableProperty] private string _agentMessageText = "";
    [ObservableProperty] private AgentMessageIntent _agentMessageIntent = AgentMessageIntent.Task;
    [ObservableProperty] private string _lastDeliverySummary = "";

    [ObservableProperty] private CollaborationRoom? _selectedRoom;
    [ObservableProperty] private string _newRoomName = "";
    [ObservableProperty] private RoomRole _inviteRole = RoomRole.Collaborator;
    [ObservableProperty] private string _latestInviteToken = "";
    [ObservableProperty] private string _joinToken = "";
    [ObservableProperty] private string _roomMessageText = "";
    [ObservableProperty] private string _roomTargetAgentId = "";

    [ObservableProperty] private DiscoveredAgentRowViewModel? _selectedDiscoveredAgent;
    [ObservableProperty] private string _discoveryQuery = "";
    [ObservableProperty] private bool _allowMessaging = true;
    [ObservableProperty] private bool _allowTaskDelegation;
    [ObservableProperty] private bool _allowEventSubscription = true;
    [ObservableProperty] private bool _allowRoomParticipation;
    [ObservableProperty] private bool _allowFileAccess;
    [ObservableProperty] private bool _allowToolControl;

    [ObservableProperty] private string _channelAdapterKind = "webhook";
    [ObservableProperty] private string _channelKey = "";
    [ObservableProperty] private string _channelTargetAgentId = "";
    [ObservableProperty] private string _channelOutboundUrl = "";

    /** Binds directly to a connected mesh capability (useful for tests and standalone hosts). */
    public void AttachMesh(IWorkspaceMesh? mesh, string machineName = MachineSpec.LocalName, IWorkspaceHost? host = null)
    {
        StopWatch();
        _directMesh = mesh;
        _directHost = host;
        if (!MachineNames.Contains(machineName))
            MachineNames.Add(machineName);
        SelectedMachine = machineName;
        IsConnected = mesh is not null;
        if (mesh is not null)
            StartWatch(mesh);
    }

    partial void OnSelectedMachineChanged(string value)
    {
        if (_disposed || _directMesh is not null)
            return;
        StopWatch();
        _ = RefreshAsync(CancellationToken.None);
    }

    partial void OnSelectedRoomChanged(CollaborationRoom? value)
    {
        if (_disposed || value is null)
        {
            RoomMessages.Clear();
            return;
        }
        _ = LoadRoomMessagesAsync(value.Id.Value, CancellationToken.None);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            return;
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            IsBusy = true;
            var (mesh, host) = await ResolveMeshAsync(cancellationToken).ConfigureAwait(false);
            if (mesh is null)
            {
                StopWatch();
                IsConnected = false;
                Status = $"{SelectedMachine} 未连接或不支持 0.5.0 智能体网络能力。";
                return;
            }
            IsConnected = true;
            StartWatch(mesh);

            var supportsMesh = host?.Supports(MeshProtocol.CapabilityMesh) ?? true;
            var supportsRooms = host?.Supports(MeshProtocol.CapabilityRooms) ?? true;
            var supportsDiscovery = host?.Supports(MeshProtocol.CapabilityDiscovery) ?? true;
            var supportsChannels = host?.Supports(MeshProtocol.CapabilityChannels) ?? true;

            if (supportsMesh)
            {
                var agents = await TryCallAsync(() => mesh.ListAgentsAsync(cancellationToken), []).ConfigureAwait(false);
                var targets = await TryCallAsync(() => mesh.ListTargetsAsync(cancellationToken), []).ConfigureAwait(false);
                SyncAgents(agents);
                SyncCollection(Targets, targets);
            }
            if (supportsRooms)
            {
                var rooms = await TryCallAsync(() => mesh.ListRoomsAsync(cancellationToken), []).ConfigureAwait(false);
                SyncRooms(rooms);
                if (SelectedRoom is { } room)
                {
                    var messages = await TryCallAsync(() => mesh.ReadRoomMessagesAsync(room.Id.Value, cancellationToken: cancellationToken), []).ConfigureAwait(false);
                    SyncCollection(RoomMessages, messages);
                }
            }
            if (supportsDiscovery)
            {
                var query = string.IsNullOrWhiteSpace(DiscoveryQuery) ? null : DiscoveryQuery.Trim();
                var cards = await TryCallAsync(() => mesh.DiscoverAgentsAsync(query, cancellationToken), []).ConfigureAwait(false);
                var trust = await TryCallAsync(() => mesh.ListTrustAsync(cancellationToken), []).ConfigureAwait(false);
                SyncDiscovery(cards, trust);
            }
            if (supportsChannels)
            {
                var channels = await TryCallAsync(() => mesh.ListChannelsAsync(cancellationToken), []).ConfigureAwait(false);
                var triggers = await TryCallAsync(() => mesh.ListTriggersAsync(cancellationToken), []).ConfigureAwait(false);
                SyncCollection(Channels, channels);
                SyncCollection(Triggers, triggers);
            }
            Status = $"已同步 {SelectedMachine}：Agents={Agents.Count}, Rooms={Rooms.Count}, Discovered={DiscoveredAgents.Count}, Channels={Channels.Count}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Status = $"刷新失败：{error.Message}";
        }
        finally
        {
            IsBusy = false;
            _refreshGate.Release();
        }
    }

    [RelayCommand]
    private async Task CreateAgentAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(NewAgentName))
        {
            Status = "请输入智能体名称。";
            return;
        }
        await ExecuteMeshMutationAsync(async mesh =>
        {
            var created = await mesh.CreateAgentAsync(new AgentProfileRegistration(
                NewAgentName.Trim(),
                string.IsNullOrWhiteSpace(NewAgentDescription) ? null : NewAgentDescription.Trim(),
                NewAgentBackendKind), cancellationToken).ConfigureAwait(false);
            NewAgentName = "";
            NewAgentDescription = "";
            await RefreshAfterMutationAsync(mesh, cancellationToken).ConfigureAwait(false);
            SelectedAgent = Agents.FirstOrDefault(agent => agent.AgentId == created.AgentId) ?? created;
            Status = $"已创建智能体 {created.Name} ({created.AgentId.Value})。";
        }, cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task WakeAgentAsync(AgentProfile? target, CancellationToken cancellationToken)
    {
        var agent = target ?? SelectedAgent;
        if (agent is null)
        {
            Status = "请先选择一个智能体。";
            return Task.CompletedTask;
        }
        return ExecuteMeshMutationAsync(async mesh =>
        {
            var woken = await mesh.WakeAgentAsync(agent.AgentId.Value, cancellationToken).ConfigureAwait(false);
            await RefreshAfterMutationAsync(mesh, cancellationToken).ConfigureAwait(false);
            SelectedAgent = Agents.FirstOrDefault(item => item.AgentId == woken.AgentId) ?? woken;
            Status = $"已唤醒 {woken.Name} ({woken.State})。";
        }, cancellationToken);
    }

    [RelayCommand]
    private Task SuspendAgentAsync(AgentProfile? target, CancellationToken cancellationToken)
    {
        var agent = target ?? SelectedAgent;
        if (agent is null)
        {
            Status = "请先选择一个智能体。";
            return Task.CompletedTask;
        }
        return ExecuteMeshMutationAsync(async mesh =>
        {
            await mesh.SuspendAgentAsync(agent.AgentId.Value, cancellationToken).ConfigureAwait(false);
            await RefreshAfterMutationAsync(mesh, cancellationToken).ConfigureAwait(false);
            Status = $"已休眠 {agent.Name}。";
        }, cancellationToken);
    }

    [RelayCommand]
    private Task SendToAgentAsync(CancellationToken cancellationToken)
    {
        var agent = SelectedAgent;
        if (agent is null)
        {
            Status = "请先选择一个目标智能体。";
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(AgentMessageText))
        {
            Status = "请输入要发送的消息内容。";
            return Task.CompletedTask;
        }
        return ExecuteMeshMutationAsync(async mesh =>
        {
            var result = await mesh.SendToAgentAsync(new RemoteMeshSendRequest(
                agent.AgentId.Value,
                AgentMessageText.Trim(),
                AgentMessageIntent,
                agent.ActiveSessionId?.Value), cancellationToken).ConfigureAwait(false);
            AgentMessageText = "";
            LastDeliverySummary = $"{result.Status} ({result.EnvelopeId})";
            Status = $"消息投递结果：{result.Status}。";
        }, cancellationToken);
    }

    [RelayCommand]
    private Task CreateRoomAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(NewRoomName))
        {
            Status = "请输入协作房间名称。";
            return Task.CompletedTask;
        }
        return ExecuteMeshMutationAsync(async mesh =>
        {
            var created = await mesh.CreateRoomAsync(NewRoomName.Trim(), new RoomPolicy(), cancellationToken).ConfigureAwait(false);
            NewRoomName = "";
            await RefreshAfterMutationAsync(mesh, cancellationToken).ConfigureAwait(false);
            SelectedRoom = Rooms.FirstOrDefault(room => room.Id == created.Id) ?? created;
            Status = $"已创建房间 {created.Name} ({created.Id.Value})。";
        }, cancellationToken);
    }

    [RelayCommand]
    private Task CreateInviteAsync(CancellationToken cancellationToken)
    {
        if (SelectedRoom is not { } room)
        {
            Status = "请先选择一个协作房间。";
            return Task.CompletedTask;
        }
        return ExecuteMeshMutationAsync(async mesh =>
        {
            var invite = await mesh.CreateInviteAsync(room.Id.Value, InviteRole, cancellationToken: cancellationToken).ConfigureAwait(false);
            LatestInviteToken = invite.Token;
            Status = $"已生成 {InviteRole} 邀请令牌。";
        }, cancellationToken);
    }

    [RelayCommand]
    private Task JoinRoomAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(JoinToken))
        {
            Status = "请输入房间邀请令牌。";
            return Task.CompletedTask;
        }
        return ExecuteMeshMutationAsync(async mesh =>
        {
            var joined = await mesh.JoinRoomAsync(JoinToken.Trim(), cancellationToken: cancellationToken).ConfigureAwait(false);
            JoinToken = "";
            await RefreshAfterMutationAsync(mesh, cancellationToken).ConfigureAwait(false);
            SelectedRoom = Rooms.FirstOrDefault(room => room.Id == joined.Id) ?? joined;
            Status = $"已加入房间 {joined.Name}。";
        }, cancellationToken);
    }

    [RelayCommand]
    private Task PostRoomMessageAsync(CancellationToken cancellationToken)
    {
        if (SelectedRoom is not { } room)
        {
            Status = "请先选择一个协作房间。";
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(RoomMessageText))
        {
            Status = "请输入房间消息内容。";
            return Task.CompletedTask;
        }
        return ExecuteMeshMutationAsync(async mesh =>
        {
            var target = string.IsNullOrWhiteSpace(RoomTargetAgentId) ? null : RoomTargetAgentId.Trim();
            await mesh.PostRoomMessageAsync(room.Id.Value, RoomMessageText.Trim(), target, cancellationToken: cancellationToken).ConfigureAwait(false);
            RoomMessageText = "";
            var messages = await mesh.ReadRoomMessagesAsync(room.Id.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
            SyncCollection(RoomMessages, messages);
            Status = $"已在房间 {room.Name} 发送消息。";
        }, cancellationToken);
    }

    [RelayCommand]
    private Task DiscoverAsync(CancellationToken cancellationToken)
        => ExecuteMeshMutationAsync(async mesh =>
        {
            var query = string.IsNullOrWhiteSpace(DiscoveryQuery) ? null : DiscoveryQuery.Trim();
            var cards = await mesh.DiscoverAgentsAsync(query, cancellationToken).ConfigureAwait(false);
            var trust = await mesh.ListTrustAsync(cancellationToken).ConfigureAwait(false);
            SyncDiscovery(cards, trust);
            Status = $"已发现 {DiscoveredAgents.Count} 个智能体名片。";
        }, cancellationToken);

    [RelayCommand]
    private Task GrantTrustAsync(DiscoveredAgentRowViewModel? target, CancellationToken cancellationToken)
    {
        var row = target ?? SelectedDiscoveredAgent;
        if (row is null)
        {
            Status = "请先选择一个已发现的智能体。";
            return Task.CompletedTask;
        }
        return ExecuteMeshMutationAsync(async mesh =>
        {
            var permissions = new AgentTrustPermissions(
                AllowMessaging,
                AllowTaskDelegation,
                AllowEventSubscription,
                AllowRoomParticipation,
                AllowFileAccess,
                AllowToolControl);
            var record = await mesh.GrantTrustAsync(row.CardId, permissions, cancellationToken: cancellationToken).ConfigureAwait(false);
            var cards = await mesh.DiscoverAgentsAsync(string.IsNullOrWhiteSpace(DiscoveryQuery) ? null : DiscoveryQuery.Trim(), cancellationToken).ConfigureAwait(false);
            var trust = await mesh.ListTrustAsync(cancellationToken).ConfigureAwait(false);
            SyncDiscovery(cards, trust);
            Status = $"已授予 {record.Name} 信任权限 ({record.Status})。";
        }, cancellationToken);
    }

    [RelayCommand]
    private Task RevokeTrustAsync(DiscoveredAgentRowViewModel? target, CancellationToken cancellationToken)
    {
        var row = target ?? SelectedDiscoveredAgent;
        if (row is null)
        {
            Status = "请先选择一个已发现的智能体。";
            return Task.CompletedTask;
        }
        return ExecuteMeshMutationAsync(async mesh =>
        {
            var record = await mesh.RevokeTrustAsync(row.CardId, cancellationToken: cancellationToken).ConfigureAwait(false);
            var cards = await mesh.DiscoverAgentsAsync(string.IsNullOrWhiteSpace(DiscoveryQuery) ? null : DiscoveryQuery.Trim(), cancellationToken).ConfigureAwait(false);
            var trust = await mesh.ListTrustAsync(cancellationToken).ConfigureAwait(false);
            SyncDiscovery(cards, trust);
            Status = $"已撤销 {record.Name} 的信任 ({record.Status})。";
        }, cancellationToken);
    }

    [RelayCommand]
    private Task BindChannelAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ChannelAdapterKind) || string.IsNullOrWhiteSpace(ChannelKey) || string.IsNullOrWhiteSpace(ChannelTargetAgentId))
        {
            Status = "请填写通道类型、通道键和目标智能体 ID。";
            return Task.CompletedTask;
        }
        return ExecuteMeshMutationAsync(async mesh =>
        {
            var binding = new ChannelBinding(
                $"bind-{Guid.NewGuid():N}",
                ChannelAdapterKind.Trim(),
                ChannelKey.Trim(),
                AgentId.Create(ChannelTargetAgentId.Trim()),
                SelectedRoom?.Id,
                OutboundUrl: string.IsNullOrWhiteSpace(ChannelOutboundUrl) ? null : ChannelOutboundUrl.Trim(),
                CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var saved = await mesh.BindChannelAsync(binding, cancellationToken).ConfigureAwait(false);
            ChannelKey = "";
            var channels = await mesh.ListChannelsAsync(cancellationToken).ConfigureAwait(false);
            SyncCollection(Channels, channels);
            Status = $"已绑定外部通道 {saved.AdapterKind}:{saved.ChannelKey} -> {saved.TargetAgentId.Value}。";
        }, cancellationToken);
    }

    private async Task LoadRoomMessagesAsync(string roomId, CancellationToken cancellationToken)
    {
        try
        {
            var (mesh, _) = await ResolveMeshAsync(cancellationToken).ConfigureAwait(false);
            if (mesh is null)
                return;
            var messages = await mesh.ReadRoomMessagesAsync(roomId, cancellationToken: cancellationToken).ConfigureAwait(false);
            SyncCollection(RoomMessages, messages);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Status = $"加载房间消息失败：{error.Message}";
        }
    }

    private async Task ExecuteMeshMutationAsync(Func<IWorkspaceMesh, Task> action, CancellationToken cancellationToken)
    {
        if (_disposed)
            return;
        IsBusy = true;
        try
        {
            var (mesh, _) = await ResolveMeshAsync(cancellationToken).ConfigureAwait(false);
            if (mesh is null)
            {
                Status = $"{SelectedMachine} 未连接或不支持智能体网络。";
                return;
            }
            await action(mesh).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Status = $"操作失败：{error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAfterMutationAsync(IWorkspaceMesh mesh, CancellationToken cancellationToken)
    {
        var agents = await TryCallAsync(() => mesh.ListAgentsAsync(cancellationToken), []).ConfigureAwait(false);
        var rooms = await TryCallAsync(() => mesh.ListRoomsAsync(cancellationToken), []).ConfigureAwait(false);
        SyncAgents(agents);
        SyncRooms(rooms);
    }

    private async Task<(IWorkspaceMesh? Mesh, IWorkspaceHost? Host)> ResolveMeshAsync(CancellationToken cancellationToken)
    {
        if (_directMesh is not null)
            return (_directMesh, _directHost);
        var machine = CurrentWorkspace?.Find(SelectedMachine);
        if (machine is null || machine.State != MachineConnectionState.Connected)
            return (null, null);
        var lease = await machine.WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
        return (lease.Client.GetCapability<IWorkspaceMesh>(), lease.Client);
    }

    private static async Task<T> TryCallAsync<T>(Func<Task<T>> call, T fallback)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception error) when (error is RemoteInvocationException or NotSupportedException)
        {
            return fallback;
        }
    }

    private void StartWatch(IWorkspaceMesh mesh)
    {
        CancellationTokenSource signal;
        lock (_watchGate)
        {
            if (_disposed)
                return;
            if (ReferenceEquals(_watchedMesh, mesh) && _watchCancellation is not null)
                return;
            StopWatchLocked();
            _watchedMesh = mesh;
            signal = new CancellationTokenSource();
            _watchCancellation = signal;
        }
        _ = WatchMeshChangesAsync(mesh, signal);
    }

    private void StopWatch()
    {
        lock (_watchGate)
            StopWatchLocked();
    }

    private void StopWatchLocked()
    {
        _watchedMesh = null;
        if (_watchCancellation is not { } signal)
            return;
        _watchCancellation = null;
        signal.Cancel();
    }

    private async Task WatchMeshChangesAsync(IWorkspaceMesh mesh, CancellationTokenSource signal)
    {
        try
        {
            await foreach (var change in mesh.MeshChangesAsync(signal.Token).ConfigureAwait(false))
            {
                if (change.Snapshot is { } snapshot)
                    RunOnUi(() => ApplySnapshot(snapshot));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or JsonRpcException or RemoteInvocationException)
        {
        }
        finally
        {
            lock (_watchGate)
            {
                if (ReferenceEquals(_watchCancellation, signal))
                {
                    _watchCancellation = null;
                    _watchedMesh = null;
                }
            }
            signal.Dispose();
        }
    }

    private void ApplySnapshot(RemoteMeshSnapshot snapshot)
    {
        if (_disposed)
            return;
        SyncAgents(snapshot.Agents);
        SyncCollection(Targets, snapshot.Targets);
        SyncRooms(snapshot.Rooms);
        if (snapshot.Discovered.Count > 0 || DiscoveredAgents.Count == 0)
            SyncDiscovery(snapshot.Discovered, snapshot.TrustRecords);
        else
            SyncCollection(TrustRecords, snapshot.TrustRecords);
        SyncCollection(Channels, snapshot.Channels);
        SyncCollection(Triggers, snapshot.Triggers);
    }

    private void SyncAgents(IReadOnlyList<AgentProfile> agents)
    {
        var selectedId = SelectedAgent?.AgentId;
        SyncCollection(Agents, agents);
        if (selectedId is { } id)
            SelectedAgent = Agents.FirstOrDefault(agent => agent.AgentId == id);
    }

    private void SyncRooms(IReadOnlyList<CollaborationRoom> rooms)
    {
        var selectedId = SelectedRoom?.Id;
        SyncCollection(Rooms, rooms);
        if (selectedId is { } id)
            SelectedRoom = Rooms.FirstOrDefault(room => room.Id == id);
    }

    private void SyncDiscovery(IReadOnlyList<DiscoveredAgentCard> cards, IReadOnlyList<AgentTrustRecord> trustRecords)
    {
        SyncCollection(TrustRecords, trustRecords);
        var byCard = trustRecords.ToDictionary(record => record.CardId, StringComparer.Ordinal);
        var rows = cards.Select(card => new DiscoveredAgentRowViewModel(card, byCard.GetValueOrDefault(card.CardId))).ToList();
        var selectedId = SelectedDiscoveredAgent?.CardId;
        SyncCollection(DiscoveredAgents, rows);
        if (selectedId is not null)
            SelectedDiscoveredAgent = DiscoveredAgents.FirstOrDefault(item => string.Equals(item.CardId, selectedId, StringComparison.Ordinal));
    }

    private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    private void OnWorkspaceChanged() => RunOnUi(() =>
    {
        if (_disposed)
            return;
        RefreshMachineNames();
        if (_directMesh is null)
            _ = SyncWatchWithWorkspaceAsync();
    });

    private async Task SyncWatchWithWorkspaceAsync()
    {
        if (_disposed || _directMesh is not null)
            return;
        var machine = CurrentWorkspace?.Find(SelectedMachine);
        if (machine is null || machine.State != MachineConnectionState.Connected)
        {
            StopWatch();
            IsConnected = false;
            return;
        }
        WorkspaceHostLease lease;
        try
        {
            lease = await machine.WaitConnectedAsync(CancellationToken.None);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return;
        }
        if (_disposed || _directMesh is not null)
            return;
        var mesh = lease.Client.GetCapability<IWorkspaceMesh>();
        if (mesh is null)
        {
            StopWatch();
            IsConnected = false;
            return;
        }
        bool needsRestart;
        lock (_watchGate)
            needsRestart = !ReferenceEquals(_watchedMesh, mesh) || _watchCancellation is null;
        if (needsRestart)
            await RefreshAsync(CancellationToken.None);
    }

    private void RefreshMachineNames()
    {
        if (CurrentWorkspace is not { } workspace)
            return;
        List<string> names = [.. workspace.Machines.Select(machine => machine.Machine.Name)];
        if (names.SequenceEqual(MachineNames))
            return;
        var current = SelectedMachine;
        MachineNames.Clear();
        foreach (var name in names)
            MachineNames.Add(name);
        if (MachineNames.Contains(current))
            SelectedMachine = current;
        else if (MachineNames.Count > 0)
            SelectedMachine = MachineNames[0];
    }

    private static void RunOnUi(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopWatch();
        if (_fleet is not null)
            _fleet.Changed -= OnWorkspaceChanged;
        if (_workspace is not null)
            _workspace.Changed -= OnWorkspaceChanged;
        _refreshGate.Dispose();
    }
}
