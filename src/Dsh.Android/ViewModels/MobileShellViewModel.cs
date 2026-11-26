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

    /**
     * 窗口里正文总量的上限(字符)。
     * 消息已经虚拟化, 条数不再拖慢布局; 真正占内存的是正文 —— 一条工具结果就可能上万字,
     * 所以内存按正文算。到上限才从头部裁, 常规会话压根不裁, 窗口不滑动, 也就没有裁头造成的位移。
     */
    private const int MaxWindowTextChars = 2_000_000;

    /**
     * 取窗口时让锚点落在窗口中部: 上下都留出可滚动的内容。
     * 一段长回复能横跨几百个事件, 所以下面的余量按事件条数给足, 否则落地时可能只有一两条消息。
     */
    private const int WindowCenterOffset = 300;

    /** 一个窗口装多少条事件; 锚点在中部, 因此上下各有一半, 滚到锚点不会立刻碰到边界。 */
    private const int WindowEvents = 1000;

    /** 每次上滑/下滑取多少条事件: 一个取数单位, 不代表最终能出几条消息。 */
    private const int OlderChunkEvents = 600;
    /**
     * 后台预取一次取多少条。
     * 每次请求有约一百毫秒的固定开销(隧道往返 + 服务端定位), 取一百条时它占了三分之一;
     * 一次取一大截能把它摊薄到可以忽略, 而实测这条链路一千五百条约一秒半, 用户滑完一屏远不止这么慢。
     */
    private const int PrefetchBatchEvents = 3000;

    private readonly MachineStore _store;
    private readonly PhoneToolStore _phoneToolStore;
    private readonly LocalAgentStore _localStore;
    private readonly MobileFleet _fleet;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _ignoredRequests = new(StringComparer.Ordinal);
    private CancellationTokenSource? _sessionLifetime;
    private long _sessionNavigation;
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

    /**
     * 正在等网络。
     * 只在"缓存已经空了、必须等一个往返"时置位 —— 走缓存的那一步是毫秒级, 给它转圈只会闪。
     */
    [ObservableProperty]
    private bool _isWaitingForData;

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
        _sessionNavigation++;
        await _lifetime.CancelAsync();
        await StopFollowingAsync();
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

    /** 收起时间线面板: 面板是就地展开的一块, 没有别的入口能把它关掉。 */
    [RelayCommand]
    private void CloseTimeline() => HasTimeline = false;

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
        if (_openSessionId.Length == 0 || Find(_openMachine) is not { } link || _sessionLifetime is not { } lifetime)
            return;
        var sessionId = _openSessionId;
        var token = lifetime.Token;
        HasTimeline = false;
        try
        {
            var lease = await link.WaitConnectedAsync(token);
            var result = await lease.Client.SessionEditing.ExecuteSessionCommandAsync(sessionId, command, token);
            token.ThrowIfCancellationRequested();
            if (result.Text is { Length: > 0 } output)
                Error = output;
            if (reload)
            {
                await ReloadSnapshotAsync(link, sessionId, token);
                return;
            }
            RefreshFromFleet();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
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
        var navigation = ++_sessionNavigation;
        await StopFollowingAsync();
        if (navigation != _sessionNavigation || _lifetime.IsCancellationRequested)
            return;
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
        var token = lifetime.Token;
        _sessionLifetime = lifetime;
        try
        {
            /*
             * 先取尾部快照, 再从"快照最后一条之后"开始订阅。
             * 从 0 订阅会让 host 把整段历史逐条回放一遍 —— 几千条事件挤在同一段流里,
             * 这才是长会话卡死和进入后一直下滑到不了底的来源, 快照那点数据量只是其中一小部分。
             */
            var snapshot = await LoadSnapshotAsync(link, row.Id, SnapshotTailEvents, token);
            if (snapshot is null)
                return;
            await ApplySnapshotAsync(row.Id, snapshot.Events, token);
            token.ThrowIfCancellationRequested();
            StartOlderFeed();
            await FollowSessionAsync(link, row.Id, NextSequence(snapshot), token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested)
                    Error = $"无法读取会话: {error.Message}";
            });
        }
    }

    private async Task FollowSessionAsync(IMachineLink link, string sessionId, long fromSeq, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var stream = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = stream.Token;
            var bridge = new DispatcherBridge<RemoteSessionEvent>(batch =>
            {
                if (!cancellationToken.IsCancellationRequested && !token.IsCancellationRequested)
                    ApplyEvents(batch);
            });
            var reset = false;
            await foreach (var item in link.SubscribeAsync(sessionId, fromSeq, token))
            {
                if (item.Reset)
                {
                    reset = true;
                    await stream.CancelAsync();
                    break;
                }
                bridge.Enqueue(item);
            }
            if (!reset || await ReloadSnapshotAsync(link, sessionId, cancellationToken) is not { } snapshot)
                return;
            fromSeq = NextSequence(snapshot);
        }
    }

    private static long NextSequence(RemoteSessionSnapshot snapshot)
        => snapshot.Events.Count == 0 ? snapshot.TotalEventCount : snapshot.Events[^1].Seq + 1;

    /** 取一截尾部历史; 失败时把错误显示出来并返回 null, 调用方据此停止后续动作。 */
    private async Task<RemoteSessionSnapshot?> LoadSnapshotAsync(IMachineLink link, string sessionId, int tailCount, CancellationToken cancellationToken)
    {
        try
        {
            var lease = await link.WaitConnectedAsync(cancellationToken);
            var total = await lease.Client.TotalEventCountAsync(sessionId, cancellationToken);
            var snapshot = await lease.Client.GetSessionHistoryRangeAsync(
                sessionId, Math.Max(0, total - tailCount), tailCount, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _totalEvents = snapshot.TotalEventCount;
            return snapshot;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    Error = $"无法读取会话快照: {error.Message}";
            });
            return null;
        }
    }

    private async Task<RemoteSessionSnapshot?> ReloadSnapshotAsync(IMachineLink link, string sessionId, CancellationToken cancellationToken)
    {
        if (await LoadSnapshotAsync(link, sessionId, SnapshotTailEvents, cancellationToken) is not { } snapshot)
            return null;
        ClearHistoryCaches();
        await ApplySnapshotAsync(sessionId, snapshot.Events, cancellationToken);
        return snapshot;
    }

    /** 停止跟随后台订阅并清空消息; 不动 SelectedSession, 供"换一个会话"与"返回列表"共用。 */
    private async Task StopFollowingAsync()
    {
        var lifetime = _sessionLifetime;
        _sessionLifetime = null;
        var cancellation = lifetime?.CancelAsync();
        _olderFeed = null;
        ClearHistoryCaches();
        _openSessionId = "";
        _windowFirstSeq = 0;
        _totalEvents = 0;
        _renderedThroughSeq = 0;
        _transcript = new SubagentTranscript();
        Messages.Clear();
        Error = "";
        SendCommand.NotifyCanExecuteChanged();
        if (cancellation is not null)
            await cancellation;
        lifetime?.Dispose();
    }

    private async Task CloseSessionAsync()
    {
        var navigation = ++_sessionNavigation;
        await StopFollowingAsync();
        if (navigation != _sessionNavigation)
            return;
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
        var hasNewerEvents = false;
        Messages.Batch(() =>
        {
            foreach (var item in batch)
            {
                if (item.SessionId != _openSessionId || item.Event is not { } sessionEvent)
                    continue;
                if (sessionEvent.Seq < _renderedThroughSeq)
                    continue;
                _totalEvents = Math.Max(_totalEvents, sessionEvent.Seq + 1);
                _newerExhausted = false;
                if (sessionEvent.Seq > _renderedThroughSeq)
                {
                    hasNewerEvents = true;
                    continue;
                }
                _transcript.Apply(sessionEvent, Messages);
                MarkRendered(sessionEvent.Seq);
                changed = true;
            }
            if (!changed)
                return;
            _transcript.Flush();
            TrimWindow();
        });
        if (!changed && !hasNewerEvents)
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
        string sessionId,
        IReadOnlyList<SessionEvent> events,
        CancellationToken cancellationToken,
        bool scrollToEnd = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId != _openSessionId)
            return;
        // 调用方已经决定了这一截的范围, 这里不再二次裁剪, 否则以锚点开头的窗口会被砍掉锚点。
        var window = events.ToList();
        var model = SubagentTranscript.ModelBefore(events, 0);
        Messages.Clear();
        _renderedThroughSeq = window.Count > 0 ? window[0].Seq : 0;
        _windowFirstSeq = _renderedThroughSeq;
        // 窗口整个换掉了, 之前"后面没有了"的判断不再成立。
        _newerExhausted = false;
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
        var removing = TrimCount();
        if (removing <= 0)
            return;
        // 裁头会让内容整体上移。视图必须知道裁掉了多少条, 才能把滚动位置补回去,
        // 否则用户看到的就是消息时间往回跳。
        WindowTrimmedFromStart?.Invoke(removing);
        for (var index = 0; index < removing; index += 1)
        {
            _transcript.Forget(Messages[0]);
            Messages.RemoveAt(0);
        }
    }

    /**
     * 这次要裁掉头部多少条; 正文总量没超上限就是 0 —— 常规会话永远不裁, 窗口不会滑动。
     * 统计一遍正文长度是微秒级的, 比维护一份随时会漂移的累计值可靠。
     */
    private int TrimCount()
    {
        var total = 0;
        foreach (var message in Messages)
            total += message.Text.Length;
        if (total <= MaxWindowTextChars)
            return 0;
        var removing = 0;
        while (removing < Messages.Count && total > MaxWindowTextChars)
            total -= Messages[removing++].Text.Length;
        return removing;
    }

    /** 窗口从头部裁掉了一批消息(条数); 视图据此补偿滚动位置。 */
    public event Action<int>? WindowTrimmedFromStart;

    /** 从尾部丢: 用户正在看前面那些, 头部不能动。 */
    private void TrimWindowFromEnd()
    {
        var total = 0;
        foreach (var message in Messages)
            total += message.Text.Length;
        while (total > MaxWindowTextChars && Messages.Count > 0)
        {
            var newest = Messages[^1];
            total -= newest.Text.Length;
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
        var sessionId = _openSessionId;
        IsLoadingOlder = true;
        try
        {
            var lease = await link.WaitConnectedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ClearHistoryCaches();
            // 与取窗口同时把锚点之后的备好, 而且在换窗之前就要等它到手:
            // 换窗之后才去取, 用户会先看到只有一两条消息、拖也拖不动的空窗, 等几百毫秒才补上来。
            var prefetch = PrefetchNewerCoreAsync(lease.Client, cancellationToken, fromOverride: anchorSeq);
            // 锚点落在窗口中部: 上下各留出半窗, 滚到锚点之后不会立刻又碰到某一端。
            // 事件序号就是它在历史里的下标(Seq 从 0 连续递增), 因此可以直接当下标用。
            var from = Math.Max(0, anchorSeq - WindowCenterOffset);
            var events = await FetchEventsAsync(lease.Client, sessionId, from, WindowEvents, cancellationToken);
            if (events.Count == 0)
                return;
            var total = await lease.Client.TotalEventCountAsync(sessionId, cancellationToken);
            await prefetch;
            cancellationToken.ThrowIfCancellationRequested();
            _totalEvents = total;
            // 这一截是为了停在锚点上, 渲染完不要被拉到末尾。
            await ApplySnapshotAsync(sessionId, events, cancellationToken, scrollToEnd: false);
            ScrollToSeqRequested?.Invoke(anchorSeq);
            // 往上那一侧的数据由后台补齐。
            StartOlderFeed();
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

    private static async Task<IReadOnlyList<SessionEvent>> FetchEventsAsync(
        RemoteHostClient client,
        string sessionId,
        long from,
        int count,
        CancellationToken cancellationToken)
        => (await client.GetSessionHistoryRangeAsync(sessionId, from, count, cancellationToken).ConfigureAwait(false)).Events;

    /** 已取到的窗口前面还有历史, 且当前没在取。 */
    public bool CanLoadOlder => !IsLoadingOlder && _windowFirstSeq > 0;

    /**
     * 已取到的窗口后面还有历史: 比的是已渲染到的事件序号, 不是最后一条消息的序号。
     * 一条消息的 Seq 是它首个 chunk 的序号, 永远落在它最后一个事件之前, 拿它判定会一直认为后面还有内容,
     * 于是每次下滑都发一次注定拿不到东西的请求。
     */
    public bool CanLoadNewer => !IsLoadingOlder && !_newerExhausted && _totalEvents > 0 && _renderedThroughSeq < _totalEvents;

    /** 上一次往后取是空的: 在拿到新事件之前不再重复问。 */
    private bool _newerExhausted;

    /**
     * 本地记的事件总数已经过期。
     * 总数只在打开会话/跳转时取过一次, 而实时订阅会不断把已渲染位置推过它 ——
     * 这时拿旧总数判断"到底了"就会提前收手, 后面还有内容却再也取不到。
     */
    public bool NewerTailStale => _renderedThroughSeq > _totalEvents;

    /**
     * 窗口已经铺到历史末尾。
     * 注意和 CanLoadNewer 的区别: 后者含"此刻是否正在装载", 装载中会短暂变成 false;
     * 拿它判断"该不该跟随末尾"会让视图在每次装载结束的瞬间被钉到新的底部, 用户接着就划不动了。
     */
    public bool WindowAtEnd => _renderedThroughSeq >= _totalEvents;






    /** 向宿主核对真实总数; 确实变多了才更新并返回 true。 */
    public async Task<bool> RefreshTailAsync()
    {
        if (_openSessionId.Length == 0 || Find(_openMachine) is not { } link)
            return false;
        try
        {
            var lease = await link.WaitConnectedAsync(_lifetime.Token).ConfigureAwait(false);
            var total = await lease.Client.TotalEventCountAsync(_openSessionId, _lifetime.Token).ConfigureAwait(false);
            if (total <= _totalEvents)
                return false;
            _totalEvents = total;
            _newerExhausted = false;
            MobileLog.Info($"tail-refresh: total={total} through={_renderedThroughSeq}");
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return false;
        }
    }

    /** 诊断用: 是否已经把"后面没有了"当成事实。 */
    public bool NewerExhausted => _newerExhausted;

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
     * 打开会话时预取的事件窗口, 上下各一份。
     * 两个方向必须分开存: 共用一个列表时, 往上取会把更晚的那半截清掉、往下取又会把更早的那半截清掉,
     * 于是每换一次方向都得重新走一次网络。跳转之后两个方向都会用到, 这个代价就藏不住了。
     */
    private readonly List<SessionEvent> _cachedOlder = [];

    private readonly List<SessionEvent> _cachedNewer = [];
    private long _historyGeneration;

    private void ClearHistoryCaches()
    {
        Interlocked.Increment(ref _historyGeneration);
        lock (_cachedOlder)
            _cachedOlder.Clear();
        lock (_cachedNewer)
            _cachedNewer.Clear();
    }

    /** 更早那一侧的缓存深度; 往下那一侧不设深度, 它天然只到历史末尾。 */
    private int OlderCachedCount
    {
        get
        {
            lock (_cachedOlder)
                return _cachedOlder.Count;
        }
    }

    /**
     * 视图滚到顶部时调用: 把缓存里已经备好的那一截接到上方。
     * 这里不联网: 滑动必须在本地完成, 等网络就意味着滑到哪里都要看运气;
     * 数据由后台一路取到缓存里, 取到之后会自动补上来。
     */
    public async Task LoadOlderAsync()
    {
        if (!CanLoadOlder || _openSessionId.Length == 0)
            return;
        await _windowGate.WaitAsync(_lifetime.Token);
        IsLoadingOlder = true;
        try
        {
            var added = 0;
            while (added == 0 && _windowFirstSeq > 0 && TakeCachedOlder() is { Count: > 0 } cached)
                added += await RenderIntoAsync(cached, atStart: true, cancellationToken: _lifetime.Token);
            // 缓存被取空时这一次滑动什么都没插入: 用户看到的就是"滑上去不动", 与渲染无关, 是供给跟不上。
            MobileLog.Info($"load-older: added={added} cached={OlderCachedCount} first={_windowFirstSeq}");
            if (added > 0)
                return;
            // 缓存空了: 接下来这段时间只能等后台取数, 让视图转个圈, 免得用户以为滑到头了。
            IsWaitingForData = true;
            StartOlderFeed();
        }
        finally
        {
            IsLoadingOlder = false;
            _windowGate.Release();
        }
    }

    /** 视图滚到底部时调用: 往后接一截; 起点是已渲染事件之后, 不是最后一条消息的序号。 */
    public Task<bool> LoadNewerAsync() => CanLoadNewer
        ? AppendNewerAsync(_lifetime.Token)
        : Task.FromResult(false);

    /**
     * 快滑到底之前先把后面那截备进缓存。
     * 续取一次是一个往返(几百毫秒), 等到真的贴到底部再取就必然看见白等。
     */
    public async Task PrefetchNewerAsync()
    {
        if (_openSessionId.Length == 0 || Find(_openMachine) is not { } link)
            return;
        try
        {
            var lease = await link.WaitConnectedAsync(_lifetime.Token).ConfigureAwait(false);
            await PrefetchNewerCoreAsync(lease.Client, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // 预取失败不打扰用户: 真滑到底部时会再取一次。
        }
    }

    /**
     * 后台持续往缓存里取更早的历史。
     * 滑动只在本地消费缓存, 因此滑多远都同样快; 取数追不上时用户滑到头会停住,
     * 等后台补上之后自动出现在上方 —— 这是唯一能让"滑动耗时与网络无关"的结构。
     */
    private void StartOlderFeed()
    {
        if (_sessionLifetime is not { } lifetime || _olderFeed is { IsCompleted: false })
            return;
        _olderFeed = FeedOlderAsync(lifetime.Token);
    }

    private async Task FeedOlderAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 判据只看"还有没有更早的历史", 不看 IsLoadingOlder:
            // 这个任务就是在 LoadOlderAsync 内部被叫醒的, 那时 IsLoadingOlder 正好是 true,
            // 拿 CanLoadOlder 当条件会让它一启动就退出, 缓存耗尽后再也取不到数据。
            while (!cancellationToken.IsCancellationRequested && _windowFirstSeq > 0)
            {
                // 缓存已经很深就先歇会儿, 别把连接占满; 门槛按"还能滑几屏"算, 不按条数拍。
                if (OlderCachedCount >= PrefetchDepthEvents)
                {
                    await Task.Delay(FeedIdleMilliseconds, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (await FetchOlderChunkAsync(cancellationToken).ConfigureAwait(false))
                    continue;
                // 这一趟什么都没取到(到了历史开头或者连接还没好): 歇一下再试, 不要空转占满 CPU。
                await Task.Delay(FeedIdleMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // 后台取数失败不打扰用户: 下次滑动会把后台再叫醒。
        }
    }

    /** 取一截更早的事件放进缓存; 顺序按序号, 滑动时从尾部消费。返回这次有没有取到东西。 */
    private async Task<bool> FetchOlderChunkAsync(CancellationToken cancellationToken)
    {
        if (Find(_openMachine) is not { } link)
            return false;
        var generation = Interlocked.Read(ref _historyGeneration);
        var lease = await link.WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
        long cursor;
        lock (_cachedOlder)
        {
            cursor = _cachedOlder.Count > 0 ? _cachedOlder[0].Seq : _windowFirstSeq;
        }
        if (cursor <= 0)
            return false;
        var from = Math.Max(0, cursor - PrefetchBatchEvents);
        if (from >= cursor)
            return false;
        var clock = Stopwatch.StartNew();
        var events = await FetchEventsAsync(lease.Client, _openSessionId, from, (int)(cursor - from), cancellationToken).ConfigureAwait(false);
        if (events.Count == 0)
            return false;
        lock (_cachedOlder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _historyGeneration))
                return false;
            _cachedOlder.InsertRange(0, events.Where(item => item.Seq < cursor && item.Seq < _windowFirstSeq));
        }
        var cost = clock.ElapsedMilliseconds;
        MobileLog.Info($"feed: from={from} want={(int)(cursor - from)} got={events.Count} cost={cost}ms rate={(events.Count * 1000.0 / Math.Max(1, cost)):F0}/s cached={OlderCachedCount} first={_windowFirstSeq}");
        // 数据只是进了缓存, 得有人把它渲染上去。
        // 停在顶部时滚不动也就没有 ScrollChanged, 光靠滑动永远等不到下一批, 用户看到的就是停在原处不动。
        // 这里在后台线程上, 订阅方要碰滚动容器, 必须回到 UI 线程再通知, 否则异常会把整个后台取数任务带走。
        Dispatcher.UIThread.Post(() => OlderCached?.Invoke());
        return true;
    }

    /** 后台取数任务; 同一时间只跑一个。 */
    private Task? _olderFeed;

    /** 缓存够深时后台歇多久再看一眼。 */
    private const int FeedIdleMilliseconds = 200;

    /**
     * 后台把缓存填到这么深就暂停。
     * 这条链路实测只有几百 KB/s(瓶颈在 SSH 端口转发), 一屏内容就要传大半秒;
     * 所以缓存必须远远领先用户: 深度按"还能连滑十几屏"来定, 而不是刚够下一次滑动。
     */
    private const int PrefetchDepthEvents = 9000;

    /** 从缓存里切出窗口最左边之前的一截; 缓存里没有更早的就返回空。 */
    private IReadOnlyList<SessionEvent> TakeCachedOlder()
    {
        lock (_cachedOlder)
        {
            // 比窗口更早的部分已经渲染过了, 留着只会让"缓存还有"变成假象。
            _cachedOlder.RemoveAll(item => item.Seq >= _windowFirstSeq);
            if (_cachedOlder.Count == 0)
                return [];
            if (_cachedOlder[^1].Seq + 1 != _windowFirstSeq)
            {
                _cachedOlder.Clear();
                return [];
            }
            // 只交出一截(靠近窗口的那一端), 剩下的留在缓存里给下次上滑用。
            var take = _cachedOlder.Count <= OlderChunkEvents ? [.. _cachedOlder] : _cachedOlder.GetRange(_cachedOlder.Count - OlderChunkEvents, OlderChunkEvents);
            _cachedOlder.RemoveRange(_cachedOlder.Count - take.Count, take.Count);
            return take;
        }
    }

    /**
     * 从缓存里切出窗口之后的一截, 供下滑接回。
     * 跳转到某条历史消息之后, 用户接下来大多是往下翻; 这条路径以前完全没有缓存,
     * 每滑一次都要等一次往返。
     */
    private IReadOnlyList<SessionEvent> TakeCachedNewer()
    {
        lock (_cachedNewer)
        {
            _cachedNewer.RemoveAll(item => item.Seq < _renderedThroughSeq);
            if (_cachedNewer.Count == 0)
                return [];
            if (_cachedNewer[0].Seq != _renderedThroughSeq)
            {
                _cachedNewer.Clear();
                return [];
            }
            // 靠窗口的这一端先交出去。
            var take = _cachedNewer.Count <= OlderChunkEvents ? [.. _cachedNewer] : _cachedNewer.GetRange(0, OlderChunkEvents);
            _cachedNewer.RemoveRange(0, take.Count);
            return take;
        }
    }

    /** 在队尾接上更晚的一段; 已有消息保持不动。 */
    private async Task<bool> AppendNewerAsync(CancellationToken cancellationToken)
    {
        if (_openSessionId.Length == 0 || Find(_openMachine) is not { } link || IsLoadingOlder)
            return false;
        await _windowGate.WaitAsync(cancellationToken);
        IsLoadingOlder = true;
        try
        {
            var lease = await link.WaitConnectedAsync(cancellationToken);
            var added = 0;
            // 缓存里备好的先接上: 跳转之后往下翻是最常见的后续动作, 这一段不该再等网络。
            while (added == 0 && TakeCachedNewer() is { Count: > 0 } cached)
                added += await RenderIntoAsync(cached, atStart: false, cancellationToken);
            if (added > 0)
            {
                MobileLog.Info($"append cached: added={added} left={_cachedNewer.Count}");
                _ = PrefetchNewerCoreAsync(lease.Client, CancellationToken.None);
                return true;
            }
            StartOlderFeed();
            if (TakeCachedNewer() is { Count: > 0 } fetched)
            {
                await RenderIntoAsync(fetched, atStart: false, cancellationToken);
                _ = PrefetchNewerCoreAsync(lease.Client, CancellationToken.None);
                return true;
            }
            while (true)
            {
                // 缓存见底, 这一段只能等往返。
                IsWaitingForData = true;
                var events = await FetchEventsAsync(lease.Client, _openSessionId, _renderedThroughSeq, OlderChunkEvents, cancellationToken);
                if (events.Count == 0)
                {
                    // 取不到不等于没有: _totalEvents 只在跳转时取过一次, 会话之后还在长。
                    // 这时要向宿主核对一次真实总数, 否则会停在过期的"末尾", 后面明明还有内容却再也取不到。
                    var total = await lease.Client.TotalEventCountAsync(_openSessionId, cancellationToken);
                    if (total > _renderedThroughSeq)
                    {
                        _totalEvents = total;
                        continue;
                    }
                    _totalEvents = total;
                    _newerExhausted = true;
                    return false;
                }
                // 一整段事件可能都落在同一条消息内部(流式增量), 屏幕上不会多出新内容;
                // 这时继续往后取, 直到真的接上新内容或者走到历史末尾。
                // 不需要设轮数上限: 每轮 _renderedThroughSeq 都会前进, 而历史是有限的。
                if (await RenderIntoAsync(events, atStart: false, cancellationToken) > 0)
                    break;
            }
            _ = PrefetchNewerCoreAsync(lease.Client, CancellationToken.None);
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
            IsWaitingForData = false;
            _windowGate.Release();
        }
    }


    /** 预取更晚内容的同时只允许一个在跑: 滚动事件密集时会连发好几次, 重复取同一段既慢又白耗流量。 */
    private bool _prefetchNewerBusy;

    private async Task PrefetchNewerCoreAsync(RemoteHostClient client, CancellationToken cancellationToken, long? fromOverride = null)
    {
        if (_prefetchNewerBusy || _sessionLifetime is not { } session)
            return;
        _prefetchNewerBusy = true;
        var generation = Interlocked.Read(ref _historyGeneration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
        try
        {
            long from;
            lock (_cachedNewer)
            {
                // 补到与更早那一侧相同的深度, 而不是只留一批:
                // 隧道往返实测抖到两百毫秒、偶发七百毫秒, 一批只够五六屏, 快划时会被追平。
                if (_cachedNewer.Count >= PrefetchDepthEvents)
                    return;
                from = fromOverride ?? (_cachedNewer.Count > 0 ? _cachedNewer[^1].Seq + 1 : _renderedThroughSeq);
            }
            var events = await FetchEventsAsync(client, _openSessionId, from, PrefetchBatchEvents, linked.Token);
            if (events.Count == 0)
                return;
            var floor = fromOverride ?? _renderedThroughSeq;
            lock (_cachedNewer)
            {
                if (linked.IsCancellationRequested || generation != Interlocked.Read(ref _historyGeneration))
                    return;
                foreach (var item in events.Where(item => item.Seq >= floor))
                    _cachedNewer.Add(item);
            }
            MobileLog.Info($"prefetch newer: from={from} got={_cachedNewer.Count} through={_renderedThroughSeq}");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
        }
        finally
        {
            _prefetchNewerBusy = false;
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
        var queued = Stopwatch.StartNew();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || events.Count == 0)
                return;
            // 有内容进来了就说明等待结束。
            IsWaitingForData = false;
            var wait = queued.ElapsedMilliseconds;
            if (atStart)
            {
                // 往前粘: 这一截自成一段, 用独立 transcript 渲染才不会打乱末尾的流式状态。
                var older = RenderEvents(events);
                var rendered = queued.ElapsedMilliseconds;
                Messages.Prefix(older);
                var prefixed = queued.ElapsedMilliseconds;
                TrimWindowFromEnd();
                _windowFirstSeq = events[0].Seq;
                added = older.Count;
                OlderLoaded?.Invoke(older.Count);
                // 单条超大消息会独占一次布局: 记下这一截里最长的正文和它是否处于展开态, 卡顿时才能对上是哪一条。
                var longest = older.OrderByDescending(message => message.Text.Length).FirstOrDefault();
                MobileLog.Info($"render-into: wait={wait}ms render={rendered - wait}ms prefix={prefixed - rendered}ms trim={queued.ElapsedMilliseconds - prefixed}ms count={older.Count} maxLen={longest?.Text.Length ?? 0} maxExpanded={longest?.IsExpanded ?? false} maxKind={longest?.Kind} maxSeq={longest?.Seq}");
                return;
            }
            var appended = new List<MessageViewModel>();
            foreach (var sessionEvent in events)
                _transcript.Apply(sessionEvent, appended);
            _transcript.Flush();
            Messages.Suffix(appended);
            MarkRendered(events[^1].Seq);
            // 又接上了新内容, 说明上一次"后面没有了"的判断已经过期。
            _newerExhausted = false;
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
        if (_sessionLifetime is not { } session)
            return;
        var cancellationToken = session.Token;
        // 窗口同一时刻只能有一个改动者: 装载进行中时 Messages 正在变, 这时判断"在不在窗口里"会得出
        // 错误的结论, 而且窗口重取会被正在进行的装载挡掉, 跳转就静默失效了。
        await _windowGate.WaitAsync(_lifetime.Token);
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            var low = Messages.FirstOrDefault()?.Seq ?? 0;
            var high = Messages.LastOrDefault(message => message.Seq is not null)?.Seq ?? 0;
            if (Messages.Count == 0 || seq < low || seq > high)
                await LoadWindowAroundAsync(seq, cancellationToken);
            else
                ScrollToSeqRequested?.Invoke(seq);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _windowGate.Release();
        }
    }

    /**
     * 窗口闸: 改动消息窗口的操作(上滑接、下滑接、跳转取窗)互斥。
     * 用互斥而不是轮询等待 —— 谁在改窗口, 别人就排在他后面, 不靠时间间隔去猜。
     */
    private readonly SemaphoreSlim _windowGate = new(1, 1);

    /** 视图请求滚动到某个序号(时间线跳转、窗口重取后的定位都用它)。 */
    public event Action<long>? ScrollToSeqRequested;

    /**
     * 队首插入了一批更早的消息。
     * 视图据此把滚动位置补回插入前看到的那一条, 否则内容会整体往下推, 视线被打断。
     */
    public event Action<int>? OlderLoaded;

    /** 后台把新的一截放进缓存了, 视图据此决定是否立刻续到上方(停在顶部时需要)。 */
    public event Action? OlderCached;
}






