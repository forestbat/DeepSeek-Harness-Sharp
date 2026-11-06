using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Android.Services;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;

namespace Dsh.Android.ViewModels;

public enum MobilePage
{
    Connect,
    Machines,
    Sessions,
    Chat,
}

/** 会话列表的一行: 登记表推送只改这一行, 不重建整个列表; 行上带着它属于哪台机器。 */
public sealed partial class SessionRowViewModel : ObservableObject
{
    public SessionRowViewModel(string machine, RemoteSessionSummary session)
    {
        Id = session.SessionId;
        Machine = machine;
        Update(session);
    }

    public string Id { get; }

    public string Machine { get; }

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _hasPending;

    [ObservableProperty]
    private string _activity = "";

    public string Kind { get; private set; } = "";

    public void Update(RemoteSessionSummary session)
    {
        Kind = session.Kind ?? "";
        Title = session.Title is { Length: > 0 } title ? title : session.ProjectPath ?? session.SessionId;
        Subtitle = $"{Machine} · {session.ProjectPath ?? session.SessionId}";
        Status = session.Status switch
        {
            HostSessionStatuses.Running => "运行中",
            HostSessionStatuses.WaitingForUser => "等待你",
            HostSessionStatuses.Idle => "空闲",
            HostSessionStatuses.Exited => "已退出",
            _ => session.Status,
        };
        HasPending = session.PendingCount > 0;
        Activity = FormatActivity(session.LastActivityAt);
    }

    private static string FormatActivity(long unixMilliseconds)
    {
        if (unixMilliseconds <= 0)
            return "";
        var time = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToLocalTime();
        return time.Date == DateTimeOffset.Now.Date ? time.ToString("HH:mm") : time.ToString("MM-dd HH:mm");
    }
}

/** 机器列表的一行: 名字与目标是用户填的, 其余是链路当前状态。 */
public sealed partial class MachineRowViewModel : ObservableObject
{
    public MachineRowViewModel(HostProfile profile)
    {
        Name = profile.Name;
        Profile = profile;
        Target = profile.Display;
    }

    /** 手机内嵌 host: 没有地址也没有令牌, 它就跑在本进程里。 */
    public MachineRowViewModel(string localName, string localTarget)
    {
        Name = localName;
        Profile = new HostProfile(localName, HostTransportKind.Tcp, "local", 0, "", "", "");
        Target = localTarget;
        IsLocal = true;
    }

    public bool IsLocal { get; }

    public string Name { get; }

    public HostProfile Profile { get; private set; }

    [ObservableProperty]
    private string _target = "";

    [ObservableProperty]
    private string _status = "未连接";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _sessionSummary = "";

    public bool CanConnect => !IsConnected && !IsBusy;

    public void Update(HostProfile profile)
    {
        Profile = profile;
        Target = profile.Display;
    }
}

/**
 * 手机端外壳: 同时连接多台桌面 host, 按机器查看会话、新建会话, 读事件流, 发消息, 处理审批。
 * 网络事件先在后台线程入队, 由 DispatcherBridge 合并后一次投递到 UI 线程, 避免每个 token 一次 JNI/调度。
 */
public sealed partial class MobileShellViewModel : ObservableObject, IAsyncDisposable
{
    private const int MessageScrollThrottleMilliseconds = 80;

    private readonly MachineStore _store;
    private readonly PhoneToolStore _phoneToolStore;
    private readonly LocalAgentStore _localStore;
    private readonly MobileFleet _fleet;
    private readonly DispatcherBridge<RemoteSessionEvent> _events;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _sessionLifetime;
    private PhoneMcpServer? _phoneServer;
    private LocalHarness? _local;
    private SubagentTranscript _transcript = new();
    private string _openSessionId = "";
    private string _openMachine = "";
    private string _approvalRequestId = "";
    private string _approvalMachine = "";
    private DateTime _lastScrollRequest;
    private string _editingName = "";
    private string _questionMachine = "";

    public MobileShellViewModel(MachineStore store, PhoneToolStore phoneToolStore, LocalAgentStore localStore)
    {
        _store = store;
        _phoneToolStore = phoneToolStore;
        _localStore = localStore;
        _fleet = new MobileFleet(store);
        _fleet.Changed += OnFleetChanged;
        _events = new DispatcherBridge<RemoteSessionEvent>(ApplyEvents);
        ShareInbox.Changed += DrainShares;
        DrainShares();
        var key = store.LoadPrivateKey();
        if (key is null)
        {
            store.SavePrivateKey(SshKeyPair.GeneratePrivateKeyPem());
            key = store.LoadPrivateKey();
        }
        RefreshKeyLine(key);
        var settings = phoneToolStore.Load();
        _phoneToolsEnabled = settings.Enabled;
        _phoneToolsUrl = string.Empty;
        if (settings.Enabled)
            _ = ApplyPhoneToolsAsync(true);
        Machines.Add(new MachineRowViewModel(LocalHarness.MachineName, LocalHarness.MachineName));
        var localSettings = localStore.Load();
        _localProvider = localSettings.Provider;
        _localModel = localSettings.Model;
        _localBaseUrl = localSettings.BaseUrl;
        _localApiKey = localSettings.ApiKey;
        foreach (var machine in store.Load())
            Machines.Add(new MachineRowViewModel(machine));
        // 保存过完整地址就自动连: 手机端没有稳定的桌面入口, 每次启动都要手点一次连接只会拖慢回到会话的速度。
        _ = AttachAllAsync();
    }

    public ObservableCollection<MachineRowViewModel> Machines { get; } = [];

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    /** 事件批次落到 UI 线程后触发, 视图据此把列表滚到底部。 */
    public event Action? TranscriptChanged;

    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    private string _portText = "";

    [ObservableProperty]
    private string _token = "";

    [ObservableProperty]
    private string _machineName = "";

    [ObservableProperty]
    private HostTransportKind _transport = HostTransportKind.Tcp;

    [ObservableProperty]
    private string _sshUser = "";

    [ObservableProperty]
    private string _publicKeyLine = "";

    [ObservableProperty]
    private bool _showPublicKey;

    private string _fingerprint = "";

    public bool UseSsh => Transport == HostTransportKind.Ssh;

    public bool UseTcp => Transport == HostTransportKind.Tcp;

    partial void OnTransportChanged(HostTransportKind value)
    {
        OnPropertyChanged(nameof(UseSsh));
        OnPropertyChanged(nameof(UseTcp));
        if (value == HostTransportKind.Ssh)
        {
            if (PortText.Trim() == "47321")
                PortText = "22";
        }
        else if (PortText.Trim() == "22")
        {
            PortText = "47321";
        }
    }

    [RelayCommand]
    private void UseTcpTransport() => Transport = HostTransportKind.Tcp;

    [RelayCommand]
    private void UseSshTransport() => Transport = HostTransportKind.Ssh;

    [RelayCommand]
    private void ShowKey() => RefreshKeyLine(_store.LoadPrivateKey());

    /** 首次进入 SSH 时生成一对 RSA 密钥; 私钥经 SecretBox 加密后落盘, 公钥只用于展示。 */
    private void RefreshKeyLine(string? pem)
    {
        if (pem is null)
            pem = SshKeyPair.GeneratePrivateKeyPem();
        _store.SavePrivateKey(pem);
        PublicKeyLine = SshKeyPair.PublicKeyLine(pem) ?? "";
        ShowPublicKey = PublicKeyLine.Length > 0;
    }

    [ObservableProperty]
    private string _status = "未连接";

    [ObservableProperty]
    private IBrush _statusBrush = Brushes.Gray;

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    [ObservableProperty]
    private SessionRowViewModel? _selectedSession;

    [ObservableProperty]
    private MachineRowViewModel? _selectedMachine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnect))]
    [NotifyPropertyChangedFor(nameof(ShowMachines))]
    [NotifyPropertyChangedFor(nameof(ShowSessions))]
    [NotifyPropertyChangedFor(nameof(ShowChat))]
    private MobilePage _page = MobilePage.Machines;

    [ObservableProperty]
    private bool _hasApproval;

    [ObservableProperty]
    private string _approvalTitle = "";

    [ObservableProperty]
    private string _approvalDetail = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _hasAttachment;

    [ObservableProperty]
    private string _attachmentName = "";

    [ObservableProperty]
    private string? _attachmentMediaType;

    [ObservableProperty]
    private string _attachmentBase64 = "";

    [ObservableProperty]
    private string _newSessionCwd = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSessionCommand))]
    private bool _isCreatingSession;

    [ObservableProperty]
    private bool _phoneToolsEnabled;

    [ObservableProperty]
    private string _phoneToolsUrl = "";

    [ObservableProperty]
    private string _phoneToolsError = "";

    [ObservableProperty]
    private string _localProvider = "";

    [ObservableProperty]
    private string _localModel = "";

    [ObservableProperty]
    private string _localBaseUrl = "";

    [ObservableProperty]
    private string _localApiKey = "";

    [ObservableProperty]
    private bool _localRunning;

    [ObservableProperty]
    private bool _hasNoMachines = true;

    public string PhoneToolsToggleLabel => PhoneToolsEnabled ? "停用" : "启用";

    partial void OnPhoneToolsEnabledChanged(bool value) => OnPropertyChanged(nameof(PhoneToolsToggleLabel));

    public string AttachmentSummary => $"{AttachmentName} · {AttachmentBytes / 1024} KB";

    private int AttachmentBytes => AttachmentBase64.Length * 3 / 4;

    public bool ShowConnect => Page == MobilePage.Connect;

    public bool ShowMachines => Page == MobilePage.Machines;

    public bool ShowSessions => Page == MobilePage.Sessions;

    public bool ShowChat => Page == MobilePage.Chat;

    public bool HasError => Error.Length > 0;

    public bool HasPhoneToolsError => PhoneToolsError.Length > 0;

    public string SessionScope => SelectedMachine?.Name ?? "全部机器";

    public ObservableCollection<QuestionItemViewModel> Questions { get; } = [];

    [ObservableProperty]
    private string _questionTitle = "";

    [ObservableProperty]
    private string _questionRequestId = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAnswersCommand))]
    private bool _hasQuestions;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAnswersCommand))]
    private bool _isSendingAnswers;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnPhoneToolsErrorChanged(string value) => OnPropertyChanged(nameof(HasPhoneToolsError));

    public bool IsConnected => Machines.Any(machine => machine.IsConnected);

    public string ChatTitle => SelectedSession?.Title ?? "会话";

    public string ChatSubtitle => SelectedSession?.Subtitle ?? "";

    /** 分享进来的文字/图片先在 UI 线程落到输入框或附件槽, 发送仍由用户确认。 */
    private void DrainShares()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(DrainShares);
            return;
        }
        while (ShareInbox.Take() is { } item)
        {
            if (item.Error.Length > 0)
            {
                Error = item.Error;
                continue;
            }
            if (item.Text.Length > 0)
            {
                Input = Input.Length == 0 ? item.Text : $"{Input}\n{item.Text}";
                continue;
            }
            if (item.Image.Length > 0 && item.MediaType.Length > 0)
                SetAttachment(item.Name, item.MediaType, item.Image);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _fleet.Changed -= OnFleetChanged;
        await _lifetime.CancelAsync();
        if (_local is not null)
        {
            _local.Changed -= OnLocalChanged;
            await _local.DisposeAsync();
            _local = null;
        }
        await _fleet.DisposeAsync();
        _lifetime.Dispose();
    }

    private async Task AttachAllAsync()
    {
        foreach (var row in Machines.ToList())
        {
            if (row.Profile.IsComplete)
                await _fleet.AttachAsync(row.Profile);
        }
        // 链路状态事件本身就投递到 UI 线程; 这里 await 之后可能已经在线程池上, 显式回到 UI 线程。
        await Dispatcher.UIThread.InvokeAsync(RefreshFromFleet);
    }

    // ---- 机器管理 ----

    [RelayCommand]
    private void AddMachine()
    {
        _editingName = "";
        MachineName = "";
        Host = "";
        PortText = "47321";
        Token = "";
        SshUser = "";
        _fingerprint = "";
        Transport = HostTransportKind.Tcp;
        Error = "";
        Page = MobilePage.Connect;
    }

    [RelayCommand]
    private void EditMachine(MachineRowViewModel? row)
    {
        if (row is null || row.IsLocal)
            return;
        _editingName = row.Name;
        MachineName = row.Name;
        Host = row.Profile.Host;
        PortText = row.Profile.Port.ToString();
        Token = row.Profile.Token;
        SshUser = row.Profile.SshUser;
        _fingerprint = row.Profile.SshFingerprint;
        Transport = row.Profile.Transport;
        Error = "";
        Page = MobilePage.Connect;
    }

    [RelayCommand]
    private void CancelMachine() => Page = MobilePage.Machines;

    [RelayCommand]
    private async Task SaveMachineAsync()
    {
        if (!int.TryParse(PortText.Trim(), out var port) || port is <= 0 or > 65535)
        {
            Error = "端口无效";
            return;
        }
        var name = MachineName.Trim();
        if (name.Length == 0)
        {
            Error = "请先给这台机器起个名字（例如 工作站）";
            return;
        }
        var profile = new HostProfile(
            name, Transport, Host.Trim(), port, Token.Trim(), SshUser.Trim(),
            _editingName == name ? _fingerprint : "");
        if (!profile.IsComplete)
        {
            Error = profile.Transport == HostTransportKind.Ssh ? "请填写主机、端口与 SSH 用户名" : "请填写主机与端口";
            return;
        }
        if (profile.Transport == HostTransportKind.Ssh)
            RefreshKeyLine(_store.LoadPrivateKey());
        var machines = _store.Load().Where(machine => machine.Name != name).ToList();
        machines.Add(profile);
        _store.Save(machines);
        var existing = Machines.FirstOrDefault(row => row.Name == name);
        if (existing is null)
        {
            existing = new MachineRowViewModel(profile);
            Machines.Add(existing);
        }
        existing.Update(profile);
        _editingName = name;
        Error = "";
        await _fleet.AttachAsync(profile);
        SelectedMachine = existing;
        Page = MobilePage.Sessions;
        RefreshFromFleet();
    }

    [RelayCommand]
    private async Task AttachMachine(MachineRowViewModel? row)
    {
        if (row is null)
            return;
        if (row.IsLocal)
        {
            await StartLocalAsync();
            SelectedMachine = row;
            Page = MobilePage.Sessions;
            return;
        }
        await _fleet.AttachAsync(row.Profile);
        SelectedMachine = row;
        Page = MobilePage.Sessions;
        RefreshFromFleet();
    }

    [RelayCommand]
    private async Task DetachMachine(MachineRowViewModel? row)
    {
        if (row is null)
            return;
        if (row.IsLocal)
        {
            await StopLocalAsync();
            return;
        }
        await _fleet.DetachAsync(row.Name);
        row.IsConnected = false;
        row.IsBusy = false;
        row.Status = "未连接";
        row.Detail = "";
        row.SessionSummary = "";
        RefreshFromFleet();
    }

    [RelayCommand]
    private async Task RemoveMachine(MachineRowViewModel? row)
    {
        if (row is null)
            return;
        if (row.IsLocal)
        {
            await StopLocalAsync();
            return;
        }
        await _fleet.DetachAsync(row.Name);
        _store.Save(_store.Load().Where(machine => machine.Name != row.Name).ToList());
        Machines.Remove(row);
        if (SelectedMachine == row)
            SelectedMachine = null;
        RefreshFromFleet();
    }

    // ---- 手机本机 agent ----

    /** 本机 host 与远端链路在 UI 上等价: 会话列表、审批、新建会话都走同一套逻辑。 */
    private IReadOnlyList<IMachineLink> Links
    {
        get
        {
            List<IMachineLink> links = [.. _fleet.Links];
            if (_local is not null)
                links.Add(_local);
            return links;
        }
    }

    private IMachineLink? Find(string name) => Links.FirstOrDefault(link => link.Name == name);

    [RelayCommand]
    private async Task StartLocalAsync()
    {
        _localStore.Save(new LocalAgentSettings(
            LocalProvider.Trim(), LocalModel.Trim(), LocalBaseUrl.Trim(), LocalApiKey.Trim()));
        await StopLocalAsync();
        var link = new LocalHarness(_store, _localStore);
        link.Changed += OnLocalChanged;
        _local = link;
        link.Start();
        RefreshFromFleet();
    }

    [RelayCommand]
    private async Task StopLocalAsync()
    {
        var link = _local;
        _local = null;
        if (link is not null)
        {
            link.Changed -= OnLocalChanged;
            await link.DisposeAsync();
        }
        LocalRunning = false;
        RefreshFromFleet();
    }

    private void OnLocalChanged() => Dispatcher.UIThread.Post(RefreshFromFleet);

    // ---- 会话 ----

    [RelayCommand]
    private void SelectMachine(MachineRowViewModel? row) => SelectedMachine = row;

    [RelayCommand]
    private void ShowAllMachines() => SelectedMachine = null;

    [RelayCommand]
    private void OpenMachines() => Page = MobilePage.Machines;

    partial void OnSelectedMachineChanged(MachineRowViewModel? value)
    {
        foreach (var row in Machines)
            row.IsSelected = ReferenceEquals(row, value);
        OnPropertyChanged(nameof(SessionScope));
        if (value is not null)
        {
            var link = Find(value.Name);
            if (link?.Info is { } info && NewSessionCwd.Trim().Length == 0)
                NewSessionCwd = info.HomeRoot;
        }
        RebuildSessions();
    }

    [RelayCommand(CanExecute = nameof(CanCreateSession))]
    private async Task CreateSessionAsync()
    {
        // 没选机器时用唯一/第一台已连接的: 手机上点 ＋ 的目的就是马上开始干活, 不该先去列表里挑一台。
        var row = SelectedMachine ?? Machines.FirstOrDefault(machine => machine.IsConnected);
        if (row is null)
        {
            Error = "先连接一台机器再新建会话";
            return;
        }
        var link = Find(row.Name);
        if (link is null)
        {
            Error = "这台机器还没连接";
            return;
        }
        var cwd = NewSessionCwd.Trim();
        if (cwd.Length == 0)
            cwd = link.Info?.HomeRoot ?? "";
        if (cwd.Length == 0)
        {
            Error = "请填写工作目录";
            return;
        }
        IsCreatingSession = true;
        try
        {
            var lease = await link.WaitConnectedAsync(_lifetime.Token);
            var created = await lease.Client.CreateSessionAsync(cwd, _lifetime.Token);
            Error = "";
            RefreshFromFleet();
            var createdRow = Sessions.FirstOrDefault(session => session.Id == created.SessionId)
                ?? AddSessionRow(row.Name, created);
            // 新会话还没有事件, Host 的登记表可能晚一步推送; 直接本地补一行并打开它。
            SelectedMachine = row;
            await OpenSessionAsync(createdRow);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"新建会话失败: {error.Message}";
        }
        finally
        {
            IsCreatingSession = false;
        }
    }

    private bool CanCreateSession() => !IsCreatingSession && (SelectedMachine is not null || Machines.Any(machine => machine.IsConnected));

    // ---- 手机工具(MCP) ----

    [RelayCommand]
    private async Task TogglePhoneToolsAsync()
    {
        var enabled = !PhoneToolsEnabled;
        _phoneToolStore.Save(_phoneToolStore.Load() with { Enabled = enabled });
        await ApplyPhoneToolsAsync(enabled);
    }

    private async Task ApplyPhoneToolsAsync(bool enabled)
    {
        var previous = _phoneServer;
        _phoneServer = null;
        if (previous is not null)
            await previous.DisposeAsync();
        PhoneToolsEnabled = enabled;
        PhoneToolsUrl = "";
        PhoneToolsError = "";
        if (enabled)
        {
            var settings = _phoneToolStore.Load();
            var server = new PhoneMcpServer(settings.Port, settings.Token);
            server.Start();
            _phoneServer = server;
            PhoneToolsUrl = server.Endpoint;
            PhoneToolsError = server.Error ?? "";
        }
        RefreshStatus();
    }

    // ---- 会话内容 ----

    [RelayCommand]
    private async Task BackAsync()
    {
        await CloseSessionAsync();
        Page = MobilePage.Sessions;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if ((text.Length == 0 && !HasAttachment) || _openSessionId.Length == 0)
            return;
        var link = Find(_openMachine);
        if (link is null)
            return;
        IReadOnlyList<RemoteImageBlock> images = HasAttachment && AttachmentMediaType is { Length: > 0 } mediaType
            ? [new RemoteImageBlock(mediaType, 0, 0, AttachmentName, AttachmentBase64)]
            : [];
        Input = "";
        IsSending = true;
        try
        {
            var lease = await link.WaitConnectedAsync(_lifetime.Token);
            // 与桌面端一致: 先恢复 harness 会话(agent 还活着时是空操作), 否则发给已退出会话的消息会丢。
            if (SelectedSession?.Kind == HostSessionKinds.Harness)
                await lease.Client.ResumeSessionAsync(_openSessionId, _lifetime.Token);
            await lease.Client.SendMessageAsync(_openSessionId, text, images, _lifetime.Token);
            ClearAttachment();
            Error = "";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"发送失败: {error.Message}";
            Input = text;
        }
        finally
        {
            IsSending = false;
        }
    }

    private bool CanSend() => !IsSending && (Input.Trim().Length > 0 || HasAttachment) && _openSessionId.Length > 0;

    /** 由视图的文件选择器写入; 只保留图片本身, 不持有 Android 的 content URI 或流。 */
    public void SetAttachment(string name, string mediaType, byte[] bytes)
    {
        AttachmentName = name;
        AttachmentMediaType = mediaType;
        AttachmentBase64 = Convert.ToBase64String(bytes);
        HasAttachment = true;
        OnPropertyChanged(nameof(AttachmentSummary));
        Error = "";
    }

    public void ReportError(string message) => Error = message;

    public void ClearAttachment()
    {
        HasAttachment = false;
        AttachmentName = "";
        AttachmentMediaType = null;
        AttachmentBase64 = "";
        OnPropertyChanged(nameof(AttachmentSummary));
    }

    [RelayCommand]
    private Task AllowAsync() => RespondApprovalAsync(true);

    [RelayCommand]
    private Task DenyAsync() => RespondApprovalAsync(false);

    private async Task RespondApprovalAsync(bool allow)
    {
        if (_approvalRequestId.Length == 0 || Find(_approvalMachine) is not { } link)
            return;
        try
        {
            var lease = await link.WaitConnectedAsync(_lifetime.Token);
            await lease.Client.RespondApprovalAsync(_approvalRequestId, allow, reason: null, _lifetime.Token);
            Error = "";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"审批失败: {error.Message}";
        }
    }

    private void OnFleetChanged() => Dispatcher.UIThread.Post(RefreshFromFleet);

    private void RefreshFromFleet()
    {
        foreach (var row in Machines)
        {
            var link = Find(row.Name);
            if (!row.IsLocal)
                row.Target = row.Profile.Display;
            row.Status = link?.State switch
            {
                MobileConnectionState.Connected => "已连接",
                MobileConnectionState.Connecting => "连接中…",
                MobileConnectionState.Reconnecting => "重连中…",
                _ => "未连接",
            };
            row.IsConnected = link?.State == MobileConnectionState.Connected;
            row.IsBusy = link?.State is MobileConnectionState.Connecting or MobileConnectionState.Reconnecting;
            row.Detail = link?.Detail ?? "";
            row.SessionSummary = link is null || link.Sessions.Count == 0 ? "" : $"{link.Sessions.Count} 个会话";
        }
        RebuildSessions();
        RefreshPending();
        RefreshStatus();
        CreateSessionCommand.NotifyCanExecuteChanged();
        LocalRunning = _local?.State == MobileConnectionState.Connected;
        HasNoMachines = Machines.Count == 0;
        OnPropertyChanged(nameof(IsConnected));
        if (SelectedMachine is { } selected
            && NewSessionCwd.Trim().Length == 0
            && Find(selected.Name)?.Info is { } info)
            NewSessionCwd = info.HomeRoot;
        if (Page == MobilePage.Machines && PhoneToolsEnabled)
            PhoneToolsError = _phoneServer?.Error ?? "";
    }

    /** 会话列表按当前选择聚合: 选了一台机器就只显示它, 没选就显示所有机器的会话。 */
    private void RebuildSessions()
    {
        var scope = SelectedMachine?.Name;
        var rows = Links
            .Where(link => scope is null || link.Name == scope)
            .SelectMany(link => link.Sessions.Select(session => (link.Name, Session: session)))
            .OrderByDescending(entry => entry.Session.LastActivityAt)
            .ToList();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (machine, session) in rows)
        {
            present.Add(KeyOf(machine, session.SessionId));
            var row = Sessions.FirstOrDefault(value => value.Machine == machine && value.Id == session.SessionId);
            if (row is null)
                Sessions.Add(new SessionRowViewModel(machine, session));
            else
                row.Update(session);
        }
        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!present.Contains(KeyOf(Sessions[index].Machine, Sessions[index].Id)))
                Sessions.RemoveAt(index);
        }
        // 不在当前机器前面板上的会话(被机器筛选挡掉的)在切换机器后就自然消失, 这里只负责保留可见集合。
        if (SelectedSession is not null && !Sessions.Contains(SelectedSession))
            SelectedSession = null;
    }

    private SessionRowViewModel AddSessionRow(string machine, RemoteSessionSummary session)
    {
        var row = new SessionRowViewModel(machine, session);
        Sessions.Insert(0, row);
        return row;
    }

    private static string KeyOf(string machine, string sessionId) => $"{machine}\u001f{sessionId}";

    private void RefreshPending()
    {
        var approval = Links
            .Select(link => (link.Name, Approval: link.Pending.Approvals.FirstOrDefault()))
            .FirstOrDefault(entry => entry.Approval is not null);
        _approvalRequestId = approval.Approval?.RequestId ?? "";
        _approvalMachine = _approvalRequestId.Length == 0 ? "" : approval.Name;
        HasApproval = _approvalRequestId.Length > 0;
        // 待决列表是全局的: 标题里带上机器与会话名, 避免在 A 会话里误批 B 会话的请求。
        ApprovalTitle = approval.Approval is not { } request
            ? ""
            : $"{request.Tool} 请求批准 · {approval.Name} · {SessionTitle(approval.Name, request.SessionId)}";
        ApprovalDetail = approval.Approval is not { } detail
            ? ""
            : (detail.Reason is { Length: > 0 } reason ? reason : detail.Arguments ?? "");
        var question = Links
            .Select(link => (link.Name, Question: link.Pending.Questions.FirstOrDefault()))
            .FirstOrDefault(entry => entry.Question is not null);
        ApplyQuestions(question.Name, question.Question);
    }

    /** 只在 requestId 变化时重建题目, 否则用户已经勾好的选项会被待决广播刷掉。 */
    private void ApplyQuestions(string machine, RemoteQuestionRequest? request)
    {
        var id = request?.RequestId ?? "";
        if (id == QuestionRequestId)
            return;
        foreach (var question in Questions)
            question.Changed -= OnAnswerChanged;
        Questions.Clear();
        QuestionRequestId = id;
        _questionMachine = machine;
        if (request is null)
        {
            HasQuestions = false;
            QuestionTitle = "";
            return;
        }
        var session = SessionTitle(machine, request.SessionId);
        QuestionTitle = request.Questions.Count == 1
            ? $"{request.Questions[0].Question} · {machine} · {session}"
            : $"需要你回答 {request.Questions.Count} 个问题 · {machine} · {session}";
        foreach (var item in request.Questions)
        {
            var question = new QuestionItemViewModel(item);
            question.Changed += OnAnswerChanged;
            Questions.Add(question);
        }
        HasQuestions = true;
        SubmitAnswersCommand.NotifyCanExecuteChanged();
    }

    private void OnAnswerChanged() => SubmitAnswersCommand.NotifyCanExecuteChanged();

    private bool CanSubmitAnswers()
        => !IsSendingAnswers
        && HasQuestions
        && Questions.All(question => question.SelectedLabels.Count > 0 || question.Custom.Trim().Length > 0);

    [RelayCommand(CanExecute = nameof(CanSubmitAnswers))]
    private async Task SubmitAnswersAsync()
    {
        if (QuestionRequestId.Length == 0 || Find(_questionMachine) is not { } link)
            return;
        IsSendingAnswers = true;
        try
        {
            var answers = Questions
                .Select(question => new RemoteQuestionAnswerItem(
                    question.Id,
                    question.SelectedLabels,
                    question.Custom.Trim() is { Length: > 0 } custom ? custom : null))
                .ToList();
            var lease = await link.WaitConnectedAsync(_lifetime.Token);
            var accepted = await lease.Client.RespondQuestionAsync(QuestionRequestId, answers, _lifetime.Token);
            Error = accepted ? "" : "问题已被其他客户端回答";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"提交回答失败: {error.Message}";
        }
        finally
        {
            IsSendingAnswers = false;
        }
    }

    private string SessionTitle(string machine, string sessionId)
        => Sessions.FirstOrDefault(row => row.Machine == machine && row.Id == sessionId)?.Title ?? sessionId;

    partial void OnSelectedSessionChanged(SessionRowViewModel? value)
    {
        if (value is null)
            return;
        _ = OpenSessionAsync(value);
    }

    private async Task OpenSessionAsync(SessionRowViewModel row)
    {
        await StopFollowingAsync();
        _openSessionId = row.Id;
        _openMachine = row.Machine;
        Page = MobilePage.Chat;
        OnPropertyChanged(nameof(ChatTitle));
        OnPropertyChanged(nameof(ChatSubtitle));
        if (row.Kind == HostSessionKinds.Terminal)
        {
            Error = "终端会话请在桌面端操作";
            return;
        }
        if (Find(row.Machine) is not { } link)
            return;

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sessionLifetime = lifetime;
        try
        {
            await foreach (var item in link.SubscribeAsync(row.Id, 0, lifetime.Token))
            {
                if (item.Reset)
                {
                    await ReloadSnapshotAsync(link, row.Id, lifetime.Token);
                    continue;
                }
                _events.Enqueue(item);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() => Error = $"无法读取会话: {error.Message}");
        }
    }

    private async Task ReloadSnapshotAsync(IMachineLink link, string sessionId, CancellationToken cancellationToken)
    {
        RemoteSessionSnapshot snapshot;
        try
        {
            var lease = await link.WaitConnectedAsync(cancellationToken);
            snapshot = await lease.Client.GetSessionSnapshotAsync(sessionId, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => Error = $"无法读取会话快照: {error.Message}");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || sessionId != _openSessionId)
                return;
            _transcript = new SubagentTranscript();
            Messages.Clear();
            ApplySnapshot(snapshot.Events);
        });
    }

    /** 停止跟随后台订阅并清空消息; 不动 SelectedSession, 供"换一个会话"与"返回列表"共用。 */
    private async Task StopFollowingAsync()
    {
        var lifetime = _sessionLifetime;
        _sessionLifetime = null;
        if (lifetime is not null)
        {
            await lifetime.CancelAsync();
            lifetime.Dispose();
        }
        _openSessionId = "";
        _transcript = new SubagentTranscript();
        Messages.Clear();
        Error = "";
        SendCommand.NotifyCanExecuteChanged();
    }

    private async Task CloseSessionAsync()
    {
        await StopFollowingAsync();
        SelectedSession = null;
        Page = MobilePage.Sessions;
        OnPropertyChanged(nameof(ChatTitle));
        OnPropertyChanged(nameof(ChatSubtitle));
    }

    /** 状态栏与常驻通知只显示一句汇总, 细节留给机器行。 */
    private void RefreshStatus()
    {
        var links = Links;
        var connected = links.Count(link => link.State == MobileConnectionState.Connected);
        var busy = links.Count(link => link.State is MobileConnectionState.Connecting or MobileConnectionState.Reconnecting);
        var phone = PhoneToolsEnabled ? 1 : 0;
        (Status, StatusBrush) = (connected, busy, phone) switch
        {
            (_, > 0, _) => ($"{connected}/{links.Count} 台已连接，{busy} 台连接中…", Brushes.Orange),
            (> 0, _, _) when phone == 1 => ($"{connected} 台已连接 · 手机工具已开启", Brushes.MediumSeaGreen),
            (> 0, _, _) => ($"{connected} 台已连接", Brushes.MediumSeaGreen),
            (_, _, 1) => ("手机工具已开启", Brushes.MediumSeaGreen),
            _ => ("未连接", Brushes.Gray),
        };
        RefreshServiceState();
    }

    /** 连接或手机工具任一需要常驻时, 才让前台服务活着。 */
    private void RefreshServiceState()
    {
        var active = Links.Count > 0 || PhoneToolsEnabled;
        ConnectionStatusHub.Publish(Status, active);
        if (active)
        {
            if (MainActivity.IsForeground)
                ConnectionServiceBridge.Start();
            return;
        }
        ConnectionServiceBridge.Stop();
    }

    /** UI 线程上的批量落盘: 只在这一个地方改 Messages, 后台线程不碰绑定集合。 */
    private void ApplyEvents(IReadOnlyList<RemoteSessionEvent> batch)
    {
        var changed = false;
        foreach (var item in batch)
        {
            if (item.Event is not { } sessionEvent)
                continue;
            _transcript.Apply(sessionEvent, Messages);
            changed = true;
        }
        if (!changed)
            return;
        _transcript.Flush();
        var now = DateTime.UtcNow;
        if ((now - _lastScrollRequest).TotalMilliseconds < MessageScrollThrottleMilliseconds)
            return;
        _lastScrollRequest = now;
        TranscriptChanged?.Invoke();
    }

    /** 快照是裸事件列表: 与流式事件共用同一段渲染, 只是少了外层信封。 */
    private void ApplySnapshot(IReadOnlyList<SessionEvent> events)
    {
        var changed = false;
        foreach (var sessionEvent in events)
        {
            _transcript.Apply(sessionEvent, Messages);
            changed = true;
        }
        if (!changed)
            return;
        _transcript.Flush();
        TranscriptChanged?.Invoke();
    }
}
