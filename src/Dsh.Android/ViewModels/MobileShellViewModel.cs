using System.Collections.ObjectModel;
using System.Diagnostics;
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
    LocalMachine,
    Sessions,
    Chat,
}

/** 本机 agent 的一条模型配置: 列表里一行, 编辑时把四个字段摊在表单上。 */
public sealed partial class LocalModelRowViewModel : ObservableObject
{
    public LocalModelRowViewModel(LocalModelProfile profile, bool isActive)
    {
        Name = profile.Name;
        Profile = profile;
        IsActive = isActive;
    }

    public string Name { get; }

    public LocalModelProfile Profile { get; }

    [ObservableProperty]
    private bool _isActive;

    public string Route => Profile.Route;

    public string KeyHint => Profile.ApiKey.Length > 0 ? "已保存密钥" : "未填密钥";
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

/** 时间线的一项: `/timestamp list` 的一行, 序号用于跳转/回退/分叉。 */
public sealed partial class TimelineEntryViewModel : ObservableObject
{
    private TimelineEntryViewModel(long seq, string clock, string text)
    {
        Seq = seq;
        Clock = clock;
        Text = text;
    }

    public long Seq { get; }

    public string Clock { get; }

    public string Text { get; }

    public string Label => $"#{Seq} {Clock}  {Text}";

    /** 服务端的行格式是 "#<seq> <HH:mm:ss>  <预览>"; 别的形状一律不认, 免得把噪声当条目。 */
    public static bool TryParse(string line, out TimelineEntryViewModel entry)
    {
        entry = null!;
        if (!line.StartsWith('#') || line.Length < 4)
            return false;
        var separator = line.IndexOf(' ');
        if (separator < 2 || !long.TryParse(line.AsSpan(1, separator - 1), out var seq))
            return false;
        var rest = line[(separator + 1)..].TrimStart();
        var clockEnd = rest.IndexOf(' ');
        var clock = clockEnd > 0 ? rest[..clockEnd] : rest;
        var text = clockEnd > 0 ? rest[(clockEnd + 1)..].Trim() : "";
        entry = new TimelineEntryViewModel(seq, clock, text);
        return true;
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
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _sessionSummary = "";

    /** 本机 host 跟着 App 活着, 没有"连接/断开"这回事; 只有远端机器才需要选机器去连。 */
    public bool CanConnect => !IsLocal && !IsConnected && !IsBusy;

    public bool CanDisconnect => !IsLocal && IsConnected;

    public bool CanManage => !IsLocal;

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

    /** 打开会话时只渲染尾部这一截: 手机一屏十几条, 整段历史全变控件会拖死 UI 线程。 */
    private const int SnapshotTailEvents = 200;

    /** 快照分批落地的每批事件数; 批与批之间让出 UI 线程。 */
    private const int SnapshotBatchEvents = 200;

    /** Messages 的窗口上限: 超出后从头部丢弃, 内存与控件数都不会无限增长。 */
    private const int MessageWindow = 400;

    /** 取窗口时让锚点落在窗口中部: 上下都留出可滚动的内容。 */
    private const int WindowCenterOffset = 100;

    /** 一个窗口装多少条事件; 锚点在中部, 因此上下各有一半, 滚到锚点不会立刻碰到边界。 */
    private const int WindowEvents = 300;

    /** 每次上滑/下滑取多少条事件: 一个取数单位, 不代表最终能出几条消息。 */
    private const int OlderChunkEvents = 100;

    /** 一次上滑至少要接出这么多条消息, 否则继续往前取: 尾部工具调用密集时一百个事件也未必出一条。 */
    private const int OlderChunkMessages = 6;

    /** 往回找更早消息时最多取几次: 一截落在同一条长消息内部时还要继续往前。 */
    private const int OlderChunkPages = 12;

    /** 一次上滑最多花这么久取数: 超过就先把手上的接上, 剩下的等用户继续滑。 */
    private const int OlderLoadBudgetMilliseconds = 800;

    /** 单次快照请求的字节上限: 一页装不满就继续要下一页, 所以它只影响往返次数。 */
    private const int SnapshotPageBytes = 512 * 1024;

    private readonly MachineStore _store;
    private readonly PhoneToolStore _phoneToolStore;
    private readonly LocalAgentStore _localStore;
    private readonly MobileFleet _fleet;
    private readonly DispatcherBridge<RemoteSessionEvent> _events;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _ignoredRequests = new(StringComparer.Ordinal);
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
        ReloadLocalProfiles();
        foreach (var machine in store.Load())
            Machines.Add(new MachineRowViewModel(machine));
        // 本机 host 随 App 启动; 其他机器保存过完整地址就自动连。
        _ = StartMachinesAsync();
    }

    public ObservableCollection<MachineRowViewModel> Machines { get; } = [];

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];

    public MessageCollection Messages { get; } = [];
    /** 本机 agent 的模型配置列表: 每行一条, 其中一条是当前使用的。 */
    public ObservableCollection<LocalModelRowViewModel> LocalProfiles { get; } = [];

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

    /** 一闪而过的提示(如"已复制"): 与失败分开, 免得复制成功也显示成红字。 */
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    public bool HasNotice => Notice.Length > 0;

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
    [NotifyPropertyChangedFor(nameof(ShowLocalMachine))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
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

    /** 本机全部可达地址, 一行一条; 手机换网后地址会变, 电脑侧按自己够得着的那条取。 */
    [ObservableProperty]
    private string _phoneToolsEndpoints = "";

    [ObservableProperty]
    private string _phoneToolsError = "";

    [ObservableProperty]
    private bool _localRunning;

    public string LocalRunningLabel => LocalRunning ? "运行中" : "已停止";

    partial void OnLocalRunningChanged(bool value) => OnPropertyChanged(nameof(LocalRunningLabel));

    [ObservableProperty]
    private string _localProfilesHint = "";

    [ObservableProperty]
    private string _localProfileName = "";

    [ObservableProperty]
    private string _localProvider = "";

    [ObservableProperty]
    private string _localModel = "";

    [ObservableProperty]
    private string _localBaseUrl = "";

    [ObservableProperty]
    private string _localApiKey = "";

    [ObservableProperty]
    private bool _hasNoMachines = true;

    [ObservableProperty]
    private bool _isLoadingOlder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopSessionCommand))]
    private bool _isStopping;

    public string PhoneToolsToggleLabel => PhoneToolsEnabled ? "停用" : "启用";

    partial void OnPhoneToolsEnabledChanged(bool value) => OnPropertyChanged(nameof(PhoneToolsToggleLabel));

    public string AttachmentSummary => $"{AttachmentName} · {AttachmentBytes / 1024} KB";

    private int AttachmentBytes => AttachmentBase64.Length * 3 / 4;

    public bool ShowConnect => Page == MobilePage.Connect;

    public bool ShowMachines => Page == MobilePage.Machines;

    public bool ShowLocalMachine => Page == MobilePage.LocalMachine;

    /** 当前页面还有上一级可退; 机器列表是最外层, 再按返回就交回系统退出。 */
    public bool CanGoBack => Page != MobilePage.Machines;

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

    private async Task StartMachinesAsync()
    {
        await StartLocalAsync();
        await AttachAllAsync();
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
        // 本机 host 由 App 启动, 这里的"查看会话"只是换页。
        if (!row.IsLocal)
            await _fleet.AttachAsync(row.Profile);
        SelectedMachine = row;
        Page = MobilePage.Sessions;
        RefreshFromFleet();
    }

    [RelayCommand]
    private async Task DetachMachine(MachineRowViewModel? row)
    {
        if (row is null || row.IsLocal)
            return;
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
        if (row is null || row.IsLocal)
            return;
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

    /** 机器列表里的"本机"一行点进本机页: 本机 agent 与手机工具都归在那里。 */
    [RelayCommand]
    private void OpenLocalMachine()
    {
        ReloadLocalProfiles();
        Page = MobilePage.LocalMachine;
    }

    [RelayCommand]
    private async Task StartLocalAsync()
    {
        var settings = _localStore.Load();
        if (settings.Profiles.Count == 0)
        {
            Error = "先在下面添加一条模型配置";
            return;
        }
        await StopLocalAsync();
        var link = new LocalHarness(_store, _localStore, () => [.. _fleet.Links]);
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

    // ---- 本机 agent 的模型配置 ----

    private void ReloadLocalProfiles()
    {
        var settings = _localStore.Load();
        LocalProfiles.Clear();
        var active = settings.Active;
        foreach (var profile in settings.Profiles)
            LocalProfiles.Add(new LocalModelRowViewModel(profile, ReferenceEquals(profile, active)));
        LocalProfilesHint = LocalProfiles.Count == 0
            ? "还没有配置模型。加一条之后就能在手机上直接跑 agent。"
            : $"当前模型：{active?.Route ?? "未选择"}";
    }

    // ---- DeepSeek 账号 ----

    [ObservableProperty]
    private string _accountStatus = "未登录";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private bool _isSigningIn;

    /** 本机 host 里的账号服务; 手机不自己实现登录, 直接用 harness 那套 PKCE。 */
    private Dsh.Account.IAccountService? LocalAccount => _local?.Account;

    private bool CanSignIn() => !IsSigningIn && _local is not null;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        if (LocalAccount is not { } account)
        {
            AccountStatus = "先启动本机 agent 再登录";
            return;
        }
        IsSigningIn = true;
        AccountStatus = "等待浏览器里完成登录…";
        try
        {
            account.SetEnabled(true);
            var profile = await account.SignInAsync(_lifetime.Token);
            AccountStatus = profile?.MaskedContact is { Length: > 0 } contact ? $"已登录 · {contact}" : "已登录";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AccountStatus = $"登录失败：{error.Message}";
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (LocalAccount is not { } account)
            return;
        try
        {
            await account.SignOutAsync();
            AccountStatus = "未登录";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AccountStatus = $"退出失败：{error.Message}";
        }
    }

    /** 账号服务里有授权时把状态同步到界面, 不必等用户再点一次登录。 */
    private void RefreshAccountStatus()
    {
        if (LocalAccount is not { } account)
        {
            AccountStatus = "未登录（先启动本机 agent）";
            SignInCommand.NotifyCanExecuteChanged();
            return;
        }
        AccountStatus = account.Snapshot switch
        {
            { Phase: Dsh.Account.AccountPhase.SignedIn } snapshot
                => snapshot.Profile?.MaskedContact is { Length: > 0 } contact ? $"已登录 · {contact}" : "已登录",
            { Phase: Dsh.Account.AccountPhase.WaitingBrowser } => "等待浏览器里完成登录…",
            { Phase: Dsh.Account.AccountPhase.Failed } snapshot => $"登录失败：{snapshot.FailureCode}",
            _ => "未登录",
        };
        SignInCommand.NotifyCanExecuteChanged();
    }

    /** 编辑表单回填: 传 null 表示新建, 名字留空由用户自己填。 */
    [RelayCommand]
    private void EditLocalProfile(LocalModelRowViewModel? row)
    {
        LocalProfileName = row?.Profile.Name ?? "";
        LocalProvider = row?.Profile.Provider ?? "";
        LocalModel = row?.Profile.Model ?? "";
        LocalBaseUrl = row?.Profile.BaseUrl ?? "";
        LocalApiKey = row?.Profile.ApiKey ?? "";
        LocalProfilesHint = row is null ? "新增一条模型配置" : $"编辑 {row.Name}";
    }

    [RelayCommand]
    private async Task SaveLocalProfileAsync()
    {
        var name = LocalProfileName.Trim();
        var provider = LocalProvider.Trim();
        var model = LocalModel.Trim();
        if (name.Length == 0)
        {
            LocalProfilesHint = "请先给这条配置起个名字";
            return;
        }
        if (provider.Length == 0 || model.Length == 0)
        {
            LocalProfilesHint = "供应商和模型都要填";
            return;
        }
        var settings = _localStore.Load();
        var profile = new LocalModelProfile(name, provider, model, LocalBaseUrl.Trim(), LocalApiKey.Trim());
        var profiles = settings.Profiles.Where(item => item.Name != name).ToList();
        profiles.Add(profile);
        // 第一条自动成为当前配置, 免得用户加完还要再点一次"使用"。
        var active = settings.ActiveName.Length == 0 ? name : settings.ActiveName;
        _localStore.Save(new LocalAgentSettings(profiles, active));
        ReloadLocalProfiles();
        await ApplyLocalProfileChangeAsync(profiles, active);
    }

    [RelayCommand]
    private async Task RemoveLocalProfileAsync(LocalModelRowViewModel? row)
    {
        if (row is null)
            return;
        var settings = _localStore.Load();
        var profiles = settings.Profiles.Where(item => item.Name != row.Name).ToList();
        var active = settings.ActiveName == row.Name ? profiles.FirstOrDefault()?.Name ?? "" : settings.ActiveName;
        _localStore.Save(new LocalAgentSettings(profiles, active));
        ReloadLocalProfiles();
        await ApplyLocalProfileChangeAsync(profiles, active);
    }

    /** 设为当前: 只在有配置变化时重启本机 host, 单纯换模型走 /model 不重启。 */
    [RelayCommand]
    private async Task UseLocalProfileAsync(LocalModelRowViewModel? row)
    {
        if (row is null)
            return;
        var settings = _localStore.Load();
        _localStore.Save(settings with { ActiveName = row.Name });
        ReloadLocalProfiles();
        await ApplyLocalProfileChangeAsync(settings.Profiles, row.Name);
    }

    /**
     * 配置改动后的生效方式分两种:
     * 本机 host 没在跑就什么都不用做, 下次启动自然用新配置;
     * 在跑的话, 换的是"已注册 provider 的地址/密钥"才需要重建 host, 只换模型用 /model 就够了。
     */
    private async Task ApplyLocalProfileChangeAsync(IReadOnlyList<LocalModelProfile> profiles, string activeName)
    {
        if (_local is null)
            return;
        var active = profiles.FirstOrDefault(profile => profile.Name == activeName);
        if (active is null)
            return;
        await SwitchModelAsync(active);
    }

    /** 已开会话里能直接换模型; 换不到就退回"下次新会话生效", 不打断正在跑的会话。 */
    private async Task SwitchModelAsync(LocalModelProfile profile)
    {
        if (_local is null || _openSessionId.Length == 0 || _openMachine != LocalHarness.MachineName)
        {
            LocalProfilesHint = $"{profile.Route} 将在下次新会话生效";
            return;
        }
        try
        {
            var lease = await _local.WaitConnectedAsync(_lifetime.Token);
            var result = await lease.Client.SessionEditing.ExecuteSessionCommandAsync(_openSessionId, $"/model {profile.Route}", _lifetime.Token);
            LocalProfilesHint = result.Text is { Length: > 0 } text ? text : $"已切到 {profile.Route}";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LocalProfilesHint = $"切换失败：{error.Message}（新会话仍会用 {profile.Route}）";
        }
    }

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
        PhoneToolsEndpoints = "";
        PhoneToolsError = "";
        if (enabled)
        {
            var settings = _phoneToolStore.Load();
            var server = new PhoneMcpServer(settings.Port, settings.Token);
            server.Start();
            _phoneServer = server;
            PhoneToolsUrl = server.Endpoint;
            // 手机换网后地址会变, 所以把所有可达地址都列出来: 电脑那边挑自己够得着的那条, 不用人猜。
            PhoneToolsEndpoints = string.Join('\n', server.Endpoints);
            PhoneToolsError = server.Error ?? "";
        }
        RefreshStatus();
    }

    // ---- 会话内容 ----

    [RelayCommand]
    private async Task BackAsync() => await GoBackAsync();

    /**
     * 按当前页面退一级: 会话 → 会话列表 → 机器列表 → (已经最外层)。
     * 返回 false 表示没有上一级, 调用方(系统返回键)应当交还给系统退出 App。
     */
    public async Task<bool> GoBackAsync()
    {
        switch (Page)
        {
            case MobilePage.Chat:
                await CloseSessionAsync();
                return true;
            case MobilePage.Sessions:
                Page = MobilePage.Machines;
                return true;
            case MobilePage.LocalMachine:
            case MobilePage.Connect:
                Page = MobilePage.Machines;
                return true;
            default:
                return false;
        }
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

    /**
     * 只要打开着一个可交互的会话就给出"停止"。
     * 不拿会话状态做可见条件: 手机上那份 Status 来自登记表推送, 常常滞后于真实的一轮对话,
     * 用户最需要停止的时候恰恰是状态还没刷成"运行中"的时候。点下去没有在跑的轮次是无害的空操作。
     */
    public bool CanStop => !IsStopping && _openSessionId.Length > 0 && SelectedSession?.Kind != HostSessionKinds.Terminal;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopSessionAsync()
    {
        if (_openSessionId.Length == 0 || Find(_openMachine) is not { } link)
            return;
        IsStopping = true;
        try
        {
            await InterruptAsync(link, _openSessionId);
        }
        finally
        {
            IsStopping = false;
        }
    }

    /** 会话列表行上的停止: 不用先打开那个会话, 手机上等一轮跑完往往是等不起的。 */
    [RelayCommand]
    private async Task StopSessionRowAsync(SessionRowViewModel? row)
    {
        if (row is null || row.Kind == HostSessionKinds.Terminal || Find(row.Machine) is not { } link)
            return;
        await InterruptAsync(link, row.Id);
    }

    private async Task InterruptAsync(IMachineLink link, string sessionId)
    {
        try
        {
            var lease = await link.WaitConnectedAsync(_lifetime.Token);
            await lease.Client.InterruptAsync(sessionId, _lifetime.Token);
            Error = "";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"停止失败: {error.Message}";
        }
    }

    // ---- 时间线 ----
    /** 时间线条目: 复用服务端的 /timestamp list, 本机与远端是同一份口径。 */
    public ObservableCollection<TimelineEntryViewModel> Timeline { get; } = [];

    [ObservableProperty]
    private bool _hasTimeline;

    [RelayCommand]
    private async Task OpenTimelineAsync()
    {
        if (_openSessionId.Length == 0 || Find(_openMachine) is not { } link)
            return;
        try
        {
            var lease = await link.WaitConnectedAsync(_lifetime.Token);
            var result = await lease.Client.SessionEditing.ExecuteSessionCommandAsync(_openSessionId, "/timestamp list", _lifetime.Token);
            Timeline.Clear();
            // 服务端每行形如 "#12 21:03:44  文本"; 解析失败的行直接跳过, 不让一条脏数据挡住整个面板。
            foreach (var line in (result.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TimelineEntryViewModel.TryParse(line, out var entry))
                    Timeline.Add(entry);
            }
            HasTimeline = Timeline.Count > 0;
            TimelineHint = HasTimeline ? "" : "这个会话还没有你发的消息";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"读取时间线失败: {error.Message}";
        }
    }

    [ObservableProperty]
    private string _timelineHint = "";

    /** 跳到某条消息: 就地滚到对应的事件序号, 不重载会话。 */
    [RelayCommand]
    private async Task JumpToTimelineEntryAsync(TimelineEntryViewModel? entry)
    {
        if (entry is null)
            return;
        HasTimeline = false;
        await JumpToSeqAsync(entry.Seq);
    }

    /** 回退到某条之前: 服务端截断会话后必须重取快照, 否则界面还留着一堆已经不存在的事件。 */
    [RelayCommand]
    private async Task RevertToTimelineEntryAsync(TimelineEntryViewModel? entry)
    {
        if (entry is null)
            return;
        await RunTimelineCommandAsync($"/timestamp revert #{entry.Seq}", reload: true);
    }

    /** 从某条分叉: 会话本身不变, 新会话出现在列表里。 */
    [RelayCommand]
    private async Task ForkAtTimelineEntryAsync(TimelineEntryViewModel? entry)
    {
        if (entry is null)
            return;
        await RunTimelineCommandAsync($"/timestamp fork #{entry.Seq}", reload: false);
    }

    private async Task RunTimelineCommandAsync(string command, bool reload)
    {
        if (_openSessionId.Length == 0 || Find(_openMachine) is not { } link)
            return;
        HasTimeline = false;
        try
        {
            var lease = await link.WaitConnectedAsync(_lifetime.Token);
            var result = await lease.Client.SessionEditing.ExecuteSessionCommandAsync(_openSessionId, command, _lifetime.Token);
            if (result.Text is { Length: > 0 } output)
                Error = output;
            if (reload)
            {
                await ReloadSnapshotAsync(link, _openSessionId, _lifetime.Token);
                return;
            }
            RefreshFromFleet();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"时间线操作失败: {error.Message}";
        }
    }

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

    /** 一句一闪而过的提示: 与错误分开显示, 免得复制成功也变成红字。 */
    public void ReportNotice(string message) => Notice = message;

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
        StopSessionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStop));
        LocalRunning = _local?.State == MobileConnectionState.Connected;
        RefreshAccountStatus();
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
        // 诊断用: 真机手势注入被系统挡住, 自动挑远端的那个长会话跑一次自证, 结果写 logcat。
        if (!_diagnosed && SelectedSession is null)
        {
            var remote = Sessions.FirstOrDefault(row => row.Title.Contains("安卓多机", StringComparison.Ordinal));
            if (remote is not null)
            {
                _diagnosed = true;
                _ = OpenSessionAsync(remote);
            }
        }
    }

    /** 诊断只跑一次。 */
    private bool _diagnosed;

    private SessionRowViewModel AddSessionRow(string machine, RemoteSessionSummary session)
    {
        var row = new SessionRowViewModel(machine, session);
        Sessions.Insert(0, row);
        return row;
    }

    private static string KeyOf(string machine, string sessionId) => $"{machine}\u001f{sessionId}";

    /**
     * 待决请求只认当前打开的那个会话: 别的终端(桌面 GUI)正在处理的提问与审批不在这里重复弹出,
     * 否则用户会在手机上答题、提交时才发现早被别人答了, 还把会话卡在弹窗上。
     */
    private void RefreshPending()
    {
        var scoped = _openSessionId.Length > 0 ? _openMachine : null;
        var approval = Links
            .Where(link => scoped is null ? _openSessionId.Length == 0 : link.Name == scoped)
            .Select(link => (link.Name, Approval: link.Pending.Approvals.FirstOrDefault(item =>
                !_ignoredRequests.Contains(item.RequestId) && (_openSessionId.Length == 0 || item.SessionId == _openSessionId))))
            .FirstOrDefault(entry => entry.Approval is not null);
        _approvalRequestId = approval.Approval?.RequestId ?? "";
        _approvalMachine = _approvalRequestId.Length == 0 ? "" : approval.Name;
        HasApproval = _approvalRequestId.Length > 0;
        ApprovalTitle = approval.Approval is not { } request
            ? ""
            : $"{request.Tool} 请求批准 · {approval.Name} · {SessionTitle(approval.Name, request.SessionId)}";
        ApprovalDetail = approval.Approval is not { } detail
            ? ""
            : (detail.Reason is { Length: > 0 } reason ? reason : detail.Arguments ?? "");
        var question = Links
            .Where(link => scoped is null ? _openSessionId.Length == 0 : link.Name == scoped)
            .Select(link => (link.Name, Question: link.Pending.Questions.FirstOrDefault(item =>
                !_ignoredRequests.Contains(item.RequestId) && (_openSessionId.Length == 0 || item.SessionId == _openSessionId))))
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

    /** 不是我的问题就不答: 记下这条 requestId, 别再弹出来。 */
    [RelayCommand]
    private void IgnoreQuestions()
    {
        DismissQuestion(QuestionRequestId);
        Error = "";
    }

    private void DismissQuestion(string requestId)
    {
        if (requestId.Length > 0)
            _ignoredRequests.Add(requestId);
        if (requestId != QuestionRequestId)
            return;
        foreach (var question in Questions)
            question.Changed -= OnAnswerChanged;
        Questions.Clear();
        QuestionRequestId = "";
        HasQuestions = false;
        QuestionTitle = "";
        SubmitAnswersCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void IgnoreApproval()
    {
        if (_approvalRequestId.Length > 0)
            _ignoredRequests.Add(_approvalRequestId);
        _approvalRequestId = "";
        _approvalMachine = "";
        HasApproval = false;
        ApprovalTitle = "";
        ApprovalDetail = "";
    }

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
            if (accepted)
            {
                Error = "";
                return;
            }
            // 别的终端先答了: 这条请求在本机不再有意义, 直接放掉, 别把会话卡在一个交不出去的弹窗上。
            DismissQuestion(QuestionRequestId);
            Error = "这条提问已经在别的终端回答过了";
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
            /*
             * 先取尾部快照, 再从"快照最后一条之后"开始订阅。
             * 从 0 订阅会让 host 把整段历史逐条回放一遍 —— 几千条事件挤在同一段流里,
             * 这才是长会话卡死和进入后一直下滑到不了底的来源, 快照那点数据量只是其中一小部分。
             */
            var snapshot = await LoadSnapshotAsync(link, row.Id, SnapshotTailEvents, lifetime.Token);
            if (snapshot is null)
                return;
            await ApplySnapshotAsync(link, row.Id, snapshot.Events, lifetime.Token);
            // 真机手势注入被系统挡住, 只能开屏后自己跑一次, 把"上滑慢在哪一段"写进 logcat。
            _ = Dispatcher.UIThread.InvokeAsync(async () => await DiagnoseOlderLoadAsync());
            var fromSeq = snapshot.Events.Count > 0 ? snapshot.Events[^1].Seq + 1 : 0;
            await foreach (var item in link.SubscribeAsync(row.Id, fromSeq, lifetime.Token))
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

    /** 取一截尾部历史; 失败时把错误显示出来并返回 null, 调用方据此停止后续动作。 */
    private async Task<RemoteSessionSnapshot?> LoadSnapshotAsync(IMachineLink link, string sessionId, int tailCount, CancellationToken cancellationToken)
    {
        try
        {
            var lease = await link.WaitConnectedAsync(cancellationToken);
            // 只要尾部: 几千条事件全下载下来再反序列化, 在真机上直接把进程撑爆, 而手机一屏也就十几条。
            var total = await lease.Client.TotalEventCountAsync(sessionId, cancellationToken);
            _totalEvents = total;
            var events = await FetchEventsAsync(lease.Client, sessionId, Math.Max(0, total - tailCount), tailCount, cancellationToken);
            return new RemoteSessionSnapshot(default!, events, total, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => Error = $"无法读取会话快照: {error.Message}");
            return null;
        }
    }

    private async Task ReloadSnapshotAsync(IMachineLink link, string sessionId, CancellationToken cancellationToken)
    {
        if (await LoadSnapshotAsync(link, sessionId, SnapshotTailEvents, cancellationToken) is not { } snapshot)
            return;
        await ApplySnapshotAsync(link, sessionId, snapshot.Events, cancellationToken);
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
        Messages.Batch(() =>
        {
            foreach (var item in batch)
            {
                if (item.Event is not { } sessionEvent)
                    continue;
                _transcript.Apply(sessionEvent, Messages);
                MarkRendered(sessionEvent.Seq);
                changed = true;
            }
            if (!changed)
                return;
            _transcript.Flush();
            TrimWindow();
        });
        if (!changed)
            return;
        var now = DateTime.UtcNow;
        if ((now - _lastScrollRequest).TotalMilliseconds < MessageScrollThrottleMilliseconds)
            return;
        _lastScrollRequest = now;
        TranscriptChanged?.Invoke();
    }

    /**
     * 把一截事件渲染成消息。
     * scrollToEnd=false 用于"跳转到某条"与"滑到更早": 那时要停在指定位置, 而不是被拉到末尾。
     */
    private async Task ApplySnapshotAsync(
        IMachineLink link,
        string sessionId,
        IReadOnlyList<SessionEvent> events,
        CancellationToken cancellationToken,
        bool scrollToEnd = true)
    {
        // 调用方已经决定了这一截的范围, 这里不再二次裁剪, 否则以锚点开头的窗口会被砍掉锚点。
        var window = events.ToList();
        var model = SubagentTranscript.ModelBefore(events, 0);
        Messages.Clear();
        _renderedThroughSeq = window.Count > 0 ? window[0].Seq : 0;
        _windowFirstSeq = _renderedThroughSeq;
        // 整段重放时增量不需要计时器: 它们会在同一帧里到达, 每批 Flush 一次就够了。
        _transcript = new SubagentTranscript(model) { Coalesce = false };
        for (var offset = 0; offset < window.Count; offset += SnapshotBatchEvents)
        {
            var batch = window.Skip(offset).Take(SnapshotBatchEvents).ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || sessionId != _openSessionId)
                    return;
                // 整批一起通知: 逐条 Add 会让虚拟化面板每条都重排一次。
                Messages.Batch(() =>
                {
                    foreach (var sessionEvent in batch)
                        _transcript.Apply(sessionEvent, Messages);
                    // 合并这一批的流式文本, 但先不通知视图滚动: 中途滚动会让列表一路下滑却始终到不了底。
                    _transcript.Flush();
                    MarkRendered(batch[^1].Seq);
                    TrimWindow();
                });
            });
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || sessionId != _openSessionId)
                return;
            // 重放结束, 后续是真实的流式增量, 重新交给计时器合并。
            _transcript.Coalesce = true;
            _lastScrollRequest = DateTime.UtcNow;
            if (scrollToEnd)
                TranscriptChanged?.Invoke();
        });
    }

    /** 窗口只留最近这段消息; 被裁掉的流式引用一并放掉, 免得增量继续堆在不可见的气泡上。 */
    private void TrimWindow()
    {
        while (Messages.Count > MessageWindow)
        {
            var oldest = Messages[0];
            Messages.RemoveAt(0);
            _transcript.Forget(oldest);
        }
    }

    /** 从尾部丢: 用户正在看前面那些, 头部不能动。 */
    private void TrimWindowFromEnd()
    {
        while (Messages.Count > MessageWindow)
        {
            var newest = Messages[^1];
            Messages.RemoveAt(Messages.Count - 1);
            _transcript.Forget(newest);
        }
    }

    /**
     * 把消息窗口挪到以 anchorSeq 为中心的一段。
     * 上滑看更早、下滑看更晚、时间线跳到某条, 三者都是"以某个位置为中心重取一屏", 只是锚点不同;
     * 统一走这里, 内存里永远只有一屏多一点的事件。
     */
    private async Task LoadWindowAroundAsync(long anchorSeq, CancellationToken cancellationToken)
    {
        if (_openSessionId.Length == 0 || Find(_openMachine) is not { } link || IsLoadingOlder)
            return;
        IsLoadingOlder = true;
        try
        {
            var lease = await link.WaitConnectedAsync(cancellationToken);
            // 锚点落在窗口中部: 上下各留出半窗, 滚到锚点之后不会立刻又碰到某一端。
            // 事件序号就是它在历史里的下标(Seq 从 0 连续递增), 因此可以直接当下标用。
            var from = Math.Max(0, anchorSeq - WindowCenterOffset);
            var events = await FetchEventsAsync(lease.Client, _openSessionId, from, WindowEvents, cancellationToken);
            if (events.Count == 0)
                return;
            _totalEvents = (await lease.Client.TotalEventCountAsync(_openSessionId, cancellationToken));
            // 这一截是为了停在锚点上, 渲染完不要被拉到末尾。
            await ApplySnapshotAsync(link, _openSessionId, events, cancellationToken, scrollToEnd: false);
            ScrollToSeqRequested?.Invoke(anchorSeq);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"载入消息失败: {error.Message}";
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }

    /**
     * 从 from 起取够 count 条事件。
     * 一页装不下就继续往后要, 直到条数够或者到了历史末尾。
     */
    private static async Task<IReadOnlyList<SessionEvent>> FetchEventsAsync(
        RemoteHostClient client,
        string sessionId,
        long from,
        int count,
        CancellationToken cancellationToken)
    {
        var events = new List<SessionEvent>();
        var cursor = from;
        var pages = 0;
        var slowestPage = 0L;
        var clock = Stopwatch.StartNew();
        while (events.Count < count)
        {
            var before = clock.ElapsedMilliseconds;
            var page = await client.GetSessionSnapshotFromAsync(sessionId, cursor, SnapshotPageBytes, cancellationToken);
            var pageCost = clock.ElapsedMilliseconds - before;
            if (pageCost > slowestPage)
                slowestPage = pageCost;
            pages += 1;
            if (page.Events.Count == 0)
                break;
            events.AddRange(page.Events);
            cursor = page.Events[^1].Seq + 1;
            // 取到历史末尾就没有下一页了。
            if (cursor >= page.TotalEventCount)
                break;
        }
        MobileLog.Info($"fetch-events: pages={pages} got={events.Count} want={count} elapsed={clock.ElapsedMilliseconds}ms slowestPage={slowestPage}ms");
        return events.Count > count ? events[..count] : events;
    }

    /** 已取到的窗口前面还有历史, 且当前没在取。 */
    public bool CanLoadOlder => !IsLoadingOlder && _windowFirstSeq > 0;

    /** 已取到的窗口后面还有历史: 最后一条不是历史的末尾。 */
    public bool CanLoadNewer => !IsLoadingOlder && _totalEvents > 0 && (Messages.LastOrDefault()?.Seq ?? long.MaxValue) < _totalEvents - 1;

    /** 当前窗口的消息是历史的第几段到第几段, 只为判边界: 取不到的活数据在这里不管。 */
    private long _totalEvents;

    /**
     * 已渲染到哪个事件序号(含)。
     * 不能用最后一条消息的 Seq 代替: 流式消息的 Seq 是它起始 chunk 的序号,
     * 后面还有几十条 chunk 属于同一条消息, 从那里续取会把它们当成新内容重放一遍。
     */
    private long _renderedThroughSeq;

    /**
     * 窗口里第一个事件的序号。
     * 往回取更早的消息必须从这里退, 不能用首条消息的 Seq: 一条消息可能横跨几百个事件,
     * 从它的起点再往回退一百个事件, 取回来的一整截仍在这条消息内部, 过滤后什么都不剩。
     */
    private long _windowFirstSeq;

    /** 记下这一批事件渲染到了哪里。 */
    private void MarkRendered(long lastSeq) => _renderedThroughSeq = Math.Max(_renderedThroughSeq, lastSeq + 1);

    /**
     * 视图滚到顶部时调用: 往前接一截。
     * 只看这一截, 不重取整窗: 重取会把几百条消息全部销毁再重建, 那正是"上滑要等很久"的来源。
     */
    public Task LoadOlderAsync() => CanLoadOlder
        ? PrependOlderAsync(_lifetime.Token)
        : Task.CompletedTask;

    /** 视图滚到底部时调用: 往后接一截; 起点是已渲染事件之后, 不是最后一条消息的序号。 */
    public Task<bool> LoadNewerAsync() => CanLoadNewer
        ? AppendNewerAsync(_renderedThroughSeq, _lifetime.Token)
        : Task.FromResult(false);

    /**
     * 在窗口最左边之前接一段。
     * 起点是窗口第一个事件的序号, 不是首条消息的序号: 一条消息可能横跨几百个事件,
     * 从消息起点往回退一截往往整段仍在它内部, 取回来过滤后什么都不剩, 用户看到的就是"滑了没反应"。
     * 一截不够就继续往前退, 直到真的拿到新消息或到了历史开头。
     */
    private async Task PrependOlderAsync(CancellationToken cancellationToken)
    {
        if (_openSessionId.Length == 0 || Find(_openMachine) is not { } link || IsLoadingOlder)
            return;
        IsLoadingOlder = true;
        var clock = Stopwatch.StartNew();
        try
        {
            var lease = await link.WaitConnectedAsync(cancellationToken);
            var cursor = _windowFirstSeq;
            var pages = 0;
            var added = 0;
            // 尾部常是一长串工具调用, 一百个事件可能只出一条消息; 退到"够看"为止, 否则用户滑一次什么也看不到。
            // 但没取够也要在预算内收手: 攒够一屏却让用户等好几秒, 比少接几条更糟。
            while (cursor > 0
                && pages < OlderChunkPages
                && added < OlderChunkMessages
                && clock.ElapsedMilliseconds < OlderLoadBudgetMilliseconds)
            {
                var from = Math.Max(0, cursor - OlderChunkEvents);
                var events = await FetchEventsAsync(lease.Client, _openSessionId, from, (int)(cursor - from), cancellationToken);
                pages += 1;
                // 只保留真正在这之前的: 服务端按下标给, 可能多给几条。
                var older = events.Where(item => item.Seq < cursor).ToList();
                cursor = from;
                if (older.Count == 0)
                    continue;
                added += await RenderIntoAsync(older, atStart: true, cancellationToken);
            }
            MobileLog.Info($"prepend: pages={pages} added={added} total={clock.ElapsedMilliseconds}ms window={Messages.Count} cursor={cursor}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"载入更早的消息失败: {error.Message}";
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }

    /** 在队尾接上 newestSeq 之后的一段; 已有消息保持不动。 */
    private async Task<bool> AppendNewerAsync(long newestSeq, CancellationToken cancellationToken)
    {
        if (_openSessionId.Length == 0 || Find(_openMachine) is not { } link || IsLoadingOlder)
            return false;
        IsLoadingOlder = true;
        try
        {
            var lease = await link.WaitConnectedAsync(cancellationToken);
            var events = await FetchEventsAsync(lease.Client, _openSessionId, newestSeq, OlderChunkEvents, cancellationToken);
            if (events.Count == 0)
                return false;
            await RenderIntoAsync(events, atStart: false, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"载入更晚的消息失败: {error.Message}";
            return false;
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }
    /** 这一截实际能取多少条: 不能越过历史开头, 也不超过一次增量的大小。 */

    /**
     * 把一段事件增量渲染进 Messages, 返回插入的消息条数。
     * atStart=true 时插到队首, 视图据此把滚动位置补回插入前的位置。
     * 渲染必须在 UI 线程: 流式增量会排 DispatcherTimer, 后台线程上没有那个调度器。
     * 往后接时复用当前的 transcript: 一段助手回复的流式事件后面跟着收尾事件,
     * 用新的 transcript 会把收尾事件当成一条新消息再显示一遍。
     */
    private async Task<int> RenderIntoAsync(IReadOnlyList<SessionEvent> events, bool atStart, CancellationToken cancellationToken)
    {
        var added = 0;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || events.Count == 0)
                return;
            if (atStart)
            {
                // 往前粘: 这一截自成一段, 用独立 transcript 渲染才不会打乱末尾的流式状态。
                var older = RenderEvents(events);
                Messages.Prefix(older);
                TrimWindowFromEnd();
                // 窗口左边界跟着前移。
                _windowFirstSeq = events[0].Seq;
                added = older.Count;
                OlderLoaded?.Invoke(older.Count);
                return;
            }
            var appended = new List<MessageViewModel>();
            foreach (var sessionEvent in events)
                _transcript.Apply(sessionEvent, appended);
            _transcript.Flush();
            Messages.Suffix(appended);
            MarkRendered(events[^1].Seq);
            TrimWindow();
            added = appended.Count;
            TranscriptChanged?.Invoke();
        });
        return added;
    }

    /** 把事件变成消息; 调用方保证在 UI 线程上。历史重放不排计时器, 文本由 Flush 一次合并。 */
    private static List<MessageViewModel> RenderEvents(IReadOnlyList<SessionEvent> events)
    {
        var messages = new List<MessageViewModel>();
        var transcript = new SubagentTranscript(SubagentTranscript.ModelBefore(events, 0)) { Coalesce = false };
        var clock = Stopwatch.StartNew();
        var slowest = 0L;
        SessionEvent? slowestEvent = null;
        foreach (var sessionEvent in events)
        {
            var before = clock.ElapsedMilliseconds;
            transcript.Apply(sessionEvent, messages);
            var cost = clock.ElapsedMilliseconds - before;
            if (cost > slowest)
            {
                slowest = cost;
                slowestEvent = sessionEvent;
            }
        }
        transcript.Flush();
        MobileLog.Info($"render-events: total={clock.ElapsedMilliseconds}ms events={events.Count} messages={messages.Count} slowest={slowest}ms type={slowestEvent?.Data.GetType().Name}");
        return messages;
    }

    /** 跳到某个序号: 不在当前窗口就把窗口挪过去, 不再提示"不在已载入的记录里"。 */
    public async Task JumpToSeqAsync(long seq)
    {
        var low = Messages.FirstOrDefault()?.Seq ?? 0;
        var high = Messages.LastOrDefault(message => message.Seq is not null)?.Seq ?? 0;
        if (Messages.Count == 0 || seq < low || seq > high)
        {
            await LoadWindowAroundAsync(seq, _lifetime.Token);
            return;
        }
        ScrollToSeqRequested?.Invoke(seq);
    }

    /** 视图请求滚动到某个序号(时间线跳转、窗口重取后的定位都用它)。 */
    public event Action<long>? ScrollToSeqRequested;

    /**
     * 队首插入了一批更早的消息。
     * 视图据此把滚动位置补回插入前看到的那一条, 否则内容会整体往下推, 视线被打断。
     */
    public event Action<int>? OlderLoaded;

    /**
     * 自证入口: 诊断"上滑加载慢在哪一段"。
     * 真机手势注入被系统挡住, 只能由界面在开屏后主动跑一次并写 logcat, 否则这个数字无从取得。
     */
    public async Task DiagnoseOlderLoadAsync()
    {
        if (_openSessionId.Length == 0 || Messages.Count == 0)
            return;
        MobileLog.Info($"diagnose start: total={_totalEvents} messages={Messages.Count} firstSeq={_windowFirstSeq} through={_renderedThroughSeq}");
        if (Find(_openMachine) is { } link && await link.WaitConnectedAsync(_lifetime.Token) is { } lease)
        {
            for (var probe = 0; probe < 3; probe += 1)
            {
                var ping = Stopwatch.StartNew();
                await lease.Client.PingAsync(_lifetime.Token);
                MobileLog.Info($"diagnose ping={ping.ElapsedMilliseconds}ms");
            }
        }
        for (var round = 1; round <= 8 && CanLoadOlder; round += 1)
        {
            var clock = Stopwatch.StartNew();
            await LoadOlderAsync();
            MobileLog.Info($"diagnose round={round} total={clock.ElapsedMilliseconds}ms messages={Messages.Count} firstSeq={_windowFirstSeq} canOlder={CanLoadOlder}");
        }
    }
}
