using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Workspace;
using Dsh.Gui.Services;
using Dsh.Ide.Bridge;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Transport;
using Dsh.Runtime.Events;
using Dsh.SessionQuery;
using Dsh.Ide;
using Dsh.Sdk;
using Dsh.Agent;

namespace Dsh.Gui.ViewModels;

public enum AppPage
{
    Dashboard,
    FleetSession,
    WorkspacePage,
    Chat,
    Settings,
    Market,
}

public enum ChatTab
{
    Conversation,
    Trace,
}

/** 主窗口状态: 会话目录、会话流、轨迹、输入胶囊、页面切换与统一决策入口。 */
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    public const int TracePreviewChars = 160;
    private const int ContentSearchDebounceMs = 300;
    private const int ContentSearchLimit = 50;
    private const int MinContentQueryLength = 2;

    private const int MaxToasts = 4;

    private readonly Context _ctx;
    private readonly SettingsFacade _settings;
    private readonly SessionCatalog _catalog;
    private readonly List<Func<bool>> _unsubscribers = [];
    private readonly List<MessageViewModel> _lastUserMessages = [];
    private IReadOnlyList<string> _skillNames = [];
    private readonly Dictionary<string, string> _contentMatches = new(StringComparer.Ordinal);
    private readonly Dictionary<ToolCallId, (string Name, string Arguments, MessageViewModel Message)> _pendingToolCalls = [];
    private readonly Dictionary<string, DecisionViewModel?> _pendingDialogs = new(StringComparer.Ordinal);
    private CancellationTokenSource? _contentSearch;

    private readonly HostChatSession _chat;
    private long _renderedSeq;
    private bool _renderedAny;

    /** 打开当前会话的体感探针(首屏/可输入/主线程最长阻塞): 只在 GUI 进程里量得到(方案 §6.3)。 */
    private SessionOpenProbe? _openProbe;

    /** 界面显示的回合号: 按 turn/start 在日志里出现的先后递增(与 payload 里的 turn 无关, 见 §8.1)。 */
    private int _displayTurn;
    private int _currentDisplayTurn;

    /** 当前开着的那一回合的 TurnStart 序号(没有开着的回合就是 null): 回退掉它时得据此收尾。 */
    private long? _openTurnStartSeq;

    /** 事件流里没到手、也没渲染过的序号区间(左闭右开)。

     * 跳号不再是致命错误: 记下缺的是哪一段, 后面的照常消费(尾巴必须继续流), 缺口按区间补。 */
    private readonly List<SeqRange> _gaps = [];

    /** 正在把补洞回来的迟到事件插回原位: 此时不动忙闲/统计/模型标签, 只补正文与工具卡片。 */
    private bool _fillingGap;

    /** 缺口补齐的进行中标记: 同一时刻只跑一趟, 免得同一段被反复取。 */
    private bool _gapFillRunning;

    /** 被撤回掉、不该再出现在界面上的序号区间: 迟到补洞与重放都要按它过滤。 */
    private readonly List<SeqRange> _reverted = [];
    private MessageViewModel? _openAssistant;
    private MessageViewModel? _openReasoning;

    /** 忙碌位是否由上下文整理置上: 自动压缩在回合内, 结束后不能替回合清掉 IsBusy。 */
    private bool _compactionBusy;

    /** 由插件提供的中立呈现接缝; 未安装该插件时为 null, 只是不显示整理进度。 */
    private readonly IContextMaintenancePresenter? _maintenance;
    private bool _disposed;
    private Task? _disposal;
    private readonly IEditorService? _editors;
    private readonly GuiEditorTerminalHost _editorTerminalHost;
    private readonly GuiEditorClientBinding _editorClientBinding;
    private readonly GuiPluginCoordinator _pluginCoordinator;

    [ObservableProperty]
    private EditorOpenWithViewModel? _editorPicker;
    private bool _updatingSuggestions;
    private bool _commandSuggestionsLoaded;
    private CancellationTokenSource? _suggestionCancellation;
    private readonly bool _ownsWorkbench;
    private static readonly IReadOnlyList<string> PluginSubcommands = ["list", "doctor", "add", "remove", "disable", "enable", "reload"];

    /** Local chat lifecycle and all session mutations belong to the persistent Host. */
    public MainViewModel(HarnessApp app, HostChatSession chat)
    {
        _ctx = app.Ctx;
        _maintenance = _ctx.Get<IContextMaintenancePresenter>(IContextMaintenancePresenter.ServiceName, false);
        _chat = chat;
        _pluginCoordinator = new GuiPluginCoordinator(_ctx, chat);
        var workbench = _ctx.Get<GuiWorkbench>(GuiWorkbench.ServiceName, false);
        _ownsWorkbench = workbench is null;
        Workbench = workbench ?? new GuiWorkbench();
        Home = app.Home;
        Subagents = new SubagentPanelViewModel(chat);
        Subagents.Changed += PopulateSubagentCards;
        Subagents.Failed += SetStatus;
        _settings = new SettingsFacade(_ctx, app.Home, chat.Fleet);
        _catalog = new SessionCatalog(_ctx, chat.Fleet);
        Gui = new GuiSettings(app.Home);
        var snapshot = Gui.Load();
        _workspaceView = snapshot.WorkspaceView;
        _sortMode = snapshot.SortSessions;
        _onlyWithSessions = snapshot.ShowOnlyWithSessions;
        _isSidebarVisible = snapshot.SidebarVisible;
        Preferences = new SettingsViewModel(
            _ctx, app.Home, _pluginCoordinator.RunAsync, Gui, _settings,
            async () => (await chat.ClientAsync()).Plugins);
        Preferences.Models.CollectionChanged += (_, _) => RefreshFilteredModels();
        Preferences.ReasoningEfforts.CollectionChanged += (_, _) => HasReasoningEfforts = Preferences.ReasoningEfforts.Count > 0;
        HasReasoningEfforts = Preferences.ReasoningEfforts.Count > 0;
        RefreshFilteredModels();
        Composer.PropertyChanged += OnComposerPropertyChanged;
        _unsubscribers.Add(_ctx.On<SkillsChangedNotification>(notification => { _ = LoadSkillNamesAsync(); }));
        _ = LoadSkillNamesAsync();
        _settings.Changed += OnSettingsChanged;
        RefreshTraceFilters();
        RefreshSessions();
        Workspace = CreateWorkspace(chat.Fleet);
        Workspace.CreateEditorPicker = CreateEditorPicker;
        _editorTerminalHost = new GuiEditorTerminalHost(chat.Fleet, async terminal =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (TerminalRequested is null) throw new InvalidOperationException("No terminal panel is attached.");
                TerminalRequested(terminal);
            });
        });
        var editors = _ctx.Get<IEditorService>(IEditorService.ServiceName, false);
        _editors = editors is null ? null
            : _ctx.Get<EditorExecutionContext>(EditorExecutionContext.ServiceName, false)!.BindTerminal(editors, _editorTerminalHost);
        _editorClientBinding = new GuiEditorClientBinding(_editors,
            _ctx.Get<Func<string, EditorRequest, EditorRequest>>("editorClientRequestMapper", false),
            _ctx.Get<Func<string, string, bool>>("editorClientHasMapping", false));
        chat.Fleet.Changed += OnFleetChanged;
        _activityTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshActivity());
        _activityTimer.Stop();
        _subagentWatch = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickSubagentWatch());
        _subagentWatch.Stop();
        Subagents.Changed += SyncSubagentWatch;
        chat.Reset += ShowSession;

        chat.Received += OnChatReceived;
        chat.Filled += ApplyFilled;
        chat.Failed += SetStatus;
        ShowSession();
        chat.Follow();
        Page = AppPage.Dashboard;
        Workbench.AttachViewModel(this);
    }

    private WorkspaceViewModel CreateWorkspace(FleetWorkspace fleet)
    {
        var workspace = new WorkspaceViewModel(
            fleet,
            ShowToast,
            Gui,
            Home,
            () => (_chat.Machine, _chat.Id.Value),
            () => new WorkspacePageContext(_chat.Machine, CurrentWorkspace));
        workspace.NavigationRequested += destination => Page = destination switch
        {
            WorkspaceDestination.Session => AppPage.FleetSession,
            WorkspaceDestination.Page => AppPage.WorkspacePage,
            _ => AppPage.Dashboard,
        };
        workspace.ChatRequested += (machine, id) => _ = OpenChatAsync(machine, id);
        workspace.DecisionRequested += decision => DecisionRequested?.Invoke(decision) ?? Task.FromResult<object?>(null);
        return workspace;
    }

    /** Every machine's sessions, notifications and pending requests (the dashboard, session views and notification center). */
    public WorkspaceViewModel Workspace { get; }

    public HarnessHome Home { get; }

    public Context Context => _ctx;
    internal bool IsDisposed => _disposed;

    public GuiSettings Gui { get; }

    public GuiWorkbench Workbench { get; }

    public SettingsViewModel Preferences { get; }

    public SubagentPanelViewModel Subagents { get; }

    public DeliverablesViewModel Deliverables { get; } = new();

    public ObservableCollection<WorkspaceGroupViewModel> Workspaces { get; } = [];

    public ObservableCollection<WorkspaceNodeViewModel> FileSystemNodes { get; } = [];

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public ObservableCollection<TraceItemViewModel> TraceItems { get; } = [];

    /** 按类型过滤后的轨迹列表, 视图绑定这一份。 */
    public ObservableCollection<TraceItemViewModel> TraceView { get; } = [];

    public ObservableCollection<TraceFilterViewModel> TraceFilters { get; } = [];

    public ObservableCollection<SuggestionViewModel> Suggestions { get; } = [];

    /** 输入胶囊「引用会话」列表。 */
    public ObservableCollection<SessionNodeViewModel> RecentSessions { get; } = [];

    public ObservableCollection<WorkspaceChoiceViewModel> RecentWorkspaces { get; } = [];

    /** 右下角气泡(成功/失败提示); 由 ShowToast 写入, 到点自动消失。 */
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /** 模型浮层列表: 按 ModelSearchText 子串过滤 Preferences.Models, IsCurrent 跟随当前模型标签。 */
    public ObservableCollection<ModelListItem> FilteredModels { get; } = [];

    public ComposerViewModel Composer { get; } = new();

    public TokenStatsViewModel TokenStats { get; } = new();

    public SettingsFacade Settings => _settings;

    public SessionId CurrentSessionId => _chat.Id;

    /** 视图响应此事件把某条消息滚入视野。 */
    public event Action<MessageViewModel>? ScrollRequested;

    /** 会话流内容发生变化, 视图据此保持贴底滚动。 */
    public event Action? MessagesChanged;

    /** 转录整段重建之后请视图重新落点(null = 这条会话没有记录, 直接贴最新)。 */
    public event Action<TranscriptAnchor?>? TranscriptRestoreRequested;

    /** 视图附加时要还原的阅读位置: 会话重建发生在视图创建之前, 那时事件还没有订阅者。 */
    public TranscriptAnchor? TranscriptRestoreAnchor
        => _transcriptAnchors.TryGetValue(_chat.Id.Value, out var anchor) ? anchor : null;


    /** 统一「需要你决定」窗口由视图实现: 审批与 ask_user_question 都走这里。 */
    public event Func<DecisionViewModel, Task<object?>>? DecisionRequested;

    /** 视图负责把文本写进剪贴板。 */
    public event Action<string>? CopyRequested;

    /** 请求打开一个终端窗口(本地/远端 PTY); 由视图实现。 */
    public event Action<TerminalViewModel>? TerminalRequested;

    [ObservableProperty]
    private SessionNodeViewModel? _selectedSession;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "准备就绪";

    [ObservableProperty]
    private string _sessionTitle = "";

    [ObservableProperty]
    private string _sessionSubtitle = "";

    [ObservableProperty]
    private AppPage _page = AppPage.Chat;

    [ObservableProperty]
    private ChatTab _tab = ChatTab.Conversation;

    [ObservableProperty]
    private bool _isSidebarVisible;

    [ObservableProperty]
    private string _workspaceView;

    [ObservableProperty]
    private string _sortMode;

    [ObservableProperty]
    private bool _onlyWithSessions;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private string _modelSearchText = "";

    [ObservableProperty]
    private bool _hasReasoningEfforts;

    [ObservableProperty]
    private bool _isDashboardPage;

    [ObservableProperty]
    private bool _isFleetSessionPage;

    [ObservableProperty]
    private bool _isWorkspacePage;

    [ObservableProperty]
    private bool _isChatPage = true;

    [ObservableProperty]
    private bool _isSettingsPage;

    [ObservableProperty]
    private bool _isMarketPage;

    [ObservableProperty]
    private bool _isTraceTab;

    [ObservableProperty]
    private bool _isSuggestionOpen;

    [ObservableProperty]
    private int _suggestionIndex;

    [ObservableProperty]
    private string _marketStatus = "";

    /** 当前会话的工作区完整路径(侧栏"工作区"行显示)。 */
    [ObservableProperty]
    private string _currentWorkspace = "";

    public bool IsSolutionView => WorkspaceView == GuiSettings.ViewSolution;

    public bool IsFileSystemView => WorkspaceView == GuiSettings.ViewFilesystem;

    public string WorkspaceViewLabel => IsFileSystemView ? "文件系统视图" : "解决方案视图";


    /** 只有这三页走懒加载的 ContentControl; 其他页面由各自的 Host 承载, ContentControl 必须隐藏, 否则会把枚举名画成 "Chat"/"WorkspacePage" 文本。 */
    public bool IsLazyPage => Page is AppPage.Dashboard or AppPage.Settings or AppPage.Market;

    public IReadOnlyList<SettingsNavItemViewModel> SettingsSections => Preferences.Sections;

    partial void OnIsBusyChanged(bool value)
    {
        Composer.IsBusy = value;
        if (value)
            _activityTimer.Start();
        else
            _activityTimer.Stop();
        RefreshActivity();
    }

    /** 最近一次收到事件(实时流)的时刻: 与 IsBusy 一起用来判断"还在跑"还是"已经没声了"。 */
    private DateTimeOffset _lastEventAt = DateTimeOffset.UtcNow;

    /** 取消已经发出、还在等宿主收尾的时刻; 收到 turn/end 或超时后清掉。 */
    private DateTimeOffset? _cancelSentAt;
    private const int CancelConfirmSeconds = 10;

    /** 状态行旁边那句活性: 空闲时为空, 忙时给"落后几条 / 多久没事件"。 */
    [ObservableProperty]
    private string _activityText = "";

    /** 忙的时候每秒重算一次活性: "已 N 秒没有事件"只靠事件驱动永远显示不出来(那时正好没有事件)。 */
    private readonly DispatcherTimer _activityTimer;

    /** 子代理徽标的看护表: 有 subagent 工具在飞、或还有子代理活着时每秒问一次宿主, 两种情况都收敛就停表。 */
    private readonly DispatcherTimer _subagentWatch;

    /** 还没返回的 subagent 工具调用数(工具执行里才会建出子会话, 调用开始的那一刻列表里往往还没有它)。 */
    private int _subagentCallsInFlight;

    /**
     * 落后量是**现成的**: 宿主报的总条数减去已渲染位置。
     *
     * 以前这些量只在写缓存时被读一次, 从不显示 —— 于是流一断, 所有可见信号一起静止且不报错,
     * 用户无法区分"正常完成"和"失去响应"。
     */
    private void RefreshActivity()
    {
        if (!IsBusy)
        {
            ActivityText = "";
            return;
        }
        var lag = Math.Max(0, _chat.TotalEventCount - _renderedSeq);
        var silent = DateTimeOffset.UtcNow - _lastEventAt;
        ActivityText = silent.TotalSeconds >= 10
            ? $"落后 {lag} 条 · 已 {silent.TotalSeconds:F0} 秒没有事件"
            : $"落后 {lag} 条";
    }

    partial void OnPageChanged(AppPage value)
    {
        IsDashboardPage = value == AppPage.Dashboard;
        IsFleetSessionPage = value == AppPage.FleetSession;
        IsWorkspacePage = value == AppPage.WorkspacePage;
        IsChatPage = value == AppPage.Chat;
        IsSettingsPage = value == AppPage.Settings;
        IsMarketPage = value == AppPage.Market;
        if (value != AppPage.Settings)
            Preferences.DeactivateNetwork();
        if (value is not (AppPage.Settings or AppPage.WorkspacePage))
            Workspace.Machines.DeactivateNetwork();
        OnPropertyChanged(nameof(IsLazyPage));
    }

    partial void OnTabChanged(ChatTab value) => IsTraceTab = value == ChatTab.Trace;

    partial void OnWorkspaceViewChanged(string value)
    {
        OnPropertyChanged(nameof(IsSolutionView));
        OnPropertyChanged(nameof(IsFileSystemView));
        OnPropertyChanged(nameof(WorkspaceViewLabel));
        if (!_disposed)
            Preferences.ApplyWorkspaceView(value);
    }

    partial void OnSearchTextChanged(string value)
    {
        ScheduleContentSearch(value);
        RefreshSessions();
    }

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (!IsSearchOpen)
            SearchText = "";
    }

    /** 标题/workspace 立即过滤; 内容命中走防抖后的 FTS 查询, 结果回来再刷新一次。 */
    private void ScheduleContentSearch(string query)
    {
        _contentSearch?.Cancel();
        _contentSearch?.Dispose();
        _contentSearch = null;
        _contentMatches.Clear();
        var trimmed = query.Trim();
        if (trimmed.Length < MinContentQueryLength)
            return;
        var signal = new CancellationTokenSource();
        _contentSearch = signal;
        _ = SearchContentAsync(trimmed, signal.Token);
    }

    private async Task SearchContentAsync(string query, CancellationToken signal)
    {
        try
        {
            await Task.Delay(ContentSearchDebounceMs, signal);
            var service = _ctx.Get<SessionQueryService>(SessionQueryService.ServiceName, false);
            if (service is null)
                return;
            foreach (var hit in service.Search(query, ContentSearchLimit))
                _contentMatches.TryAdd(hit.SessionId, hit.Snippet);
            if (!signal.IsCancellationRequested)
                RefreshSessions();
        }
        catch (OperationCanceledException)
        {
        }
    }

    partial void OnModelSearchTextChanged(string value) => RefreshFilteredModels();

    private void RefreshFilteredModels()
    {
        FilteredModels.Clear();
        var search = ModelSearchText.Trim();
        foreach (var model in Preferences.Models)
        {
            if (search.Length == 0 || model.Contains(search, StringComparison.OrdinalIgnoreCase))
                FilteredModels.Add(new ModelListItem(model, string.Equals(model, Composer.ModelLabel, StringComparison.Ordinal)));
        }
    }

    partial void OnOnlyWithSessionsChanged(bool value)
    {
        if (_disposed)
            return;
        Gui.Save(Gui.Load() with { ShowOnlyWithSessions = value });
        RefreshSessions();
    }


    partial void OnSelectedSessionChanged(SessionNodeViewModel? value)
    {
        foreach (var node in AllSessions())
            node.IsSelected = ReferenceEquals(node, value);
        if (value is null || _disposed)
            return;
        if (value.SessionId == _chat.Id)
            return;
        _ = SwitchToAsync(value);
    }

    [RelayCommand]
    private void RefreshSessions()
    {
        foreach (var node in AllSessions().ToList())
            node.IsSelected = false;
        Workspaces.Clear();
        FileSystemNodes.Clear();
        // 目录只取一次: 过滤后用于侧栏分组, 全量用于最近工作区。
        var catalog = _catalog.Load();
        var nodes = FilteredCatalog(catalog);
        foreach (var group in GroupByWorkspace(nodes))
            Workspaces.Add(group);
        foreach (var node in BuildFileTree(nodes))
            FileSystemNodes.Add(node);
        RecentSessions.Clear();
        foreach (var node in Sort(nodes).Take(20))
            RecentSessions.Add(node);
        SelectedSession = FindSession(_chat.Id);
        // 全量目录已在上方取到, 这里不再第二次扫描。
        RebuildRecentWorkspaces(catalog);
        UpdateCurrentWorkspace();
    }

    /** 最近工作区 = 已有会话的 distinct 完整路径, 按最近创建倒序; 选中只改默认目录。 */
    private void RebuildRecentWorkspaces(IReadOnlyList<SessionNodeViewModel> catalog)
    {
        RecentWorkspaces.Clear();
        var paths = catalog
            .Where(node => node.WorkspacePath != SessionCatalog.UnspecifiedWorkspace)
            .GroupBy(node => node.WorkspacePath, StringComparer.Ordinal)
            .OrderByDescending(group => group.Max(node => node.CreatedAt))
            .Select(group => group.Key);
        foreach (var path in paths)
        {
            var captured = path;
            RecentWorkspaces.Add(new WorkspaceChoiceViewModel(
                SessionCatalog.WorkspaceDisplayName(path),
                path,
                new RelayCommand(() => SetDefaultWorkspace(captured))));
        }
    }

    private EditorOpenWithViewModel CreateEditorPicker(string machine, string workspace)
    {
        var sessionId = Workspace.ActiveSession is { } active && active.Machine == machine
            ? active.SessionId : machine == _chat.Machine ? _chat.Id.Value : null;
        var turn = machine == _chat.Machine && sessionId == _chat.Id.Value
            ? (_chat.Events.LastOrDefault(item => item.Data is TurnStartPayload)?.Data as TurnStartPayload)?.Turn
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return new(_editors, async cancellationToken =>
        {
            var connection = _chat.Fleet.Find(machine) ?? throw new InvalidOperationException("The editor host is unavailable.");
            var lease = await connection.WaitConnectedAsync(cancellationToken);
            var editors = lease.Client.GetCapability<IRemoteEditors>()
                ?? throw new NotSupportedException("The selected Host does not provide IDE integration.");
            var service = new HostEditorService(editors);
            await service.RefreshAsync(workspace, cancellationToken, sessionId);
            return service;
        }, workspace, machine, sessionId, turn);
    }

    [RelayCommand]
    private async Task PrepareEditorPickerAsync()
    {
        EditorPicker = CreateEditorPicker(Workspace.ActiveSession?.Machine ?? _chat.Machine,
            Workspace.ActiveSession?.Card.ProjectPath ?? _chat.Header.Cwd ?? Home.Root);
        await EditorPicker.RefreshAsync();
    }
    /** 在当前会话/选中会话的机器上打开终端: 本地与远端共用同一套 TerminalViewModel。 */
    [RelayCommand]
    private void OpenTerminal() => _ = OpenTerminalAsync();

    private async Task OpenTerminalAsync()
    {
        var machine = Workspace.ActiveSession?.Machine ?? _chat.Machine;
        var connection = _chat.Fleet.Find(machine);
        if (connection is null)
        {
            ShowToast("终端启动失败", $"找不到机器 {machine}", true);
            return;
        }
        var title = machine == MachineSpec.LocalName ? "本地终端" : $"{machine} · 终端";
        var cwd = Workspace.ActiveSession?.Card.ProjectPath ?? _chat.Header.Cwd ?? Home.Root;
        var terminal = new TerminalViewModel(title);
        TerminalRequested?.Invoke(terminal);
        try
        {
            await terminal.StartOnMachineAsync(connection, cwd, title);
        }
        catch (Exception error)
        {
            ShowToast("终端启动失败", error.Message, true);
        }
    }

    /** 右下角气泡: 成功/失败都走这里(设置页“测试连接”“打开远程工作区”共用); 到点自动消失。 */
    public void ShowToast(string title, string message, bool isError)
    {
        var toast = new ToastViewModel(title, message, isError);
        Dispatcher.UIThread.Post(() =>
        {
            Toasts.Add(toast);
            while (Toasts.Count > MaxToasts)
                Toasts.RemoveAt(0);
            _ = DismissToastAsync(toast);
        });
    }

    private async Task DismissToastAsync(ToastViewModel toast)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        Dispatcher.UIThread.Post(() => Toasts.Remove(toast));
    }

    /** 只设新会话的默认目录, 不切换当前会话。 */
    private void SetDefaultWorkspace(string path)
    {
        Gui.Save(Gui.Load() with { DefaultWorkspace = path });
        StatusText = $"新会话默认工作区已设为 {path}";
        RefreshSessions();
    }

    private void UpdateCurrentWorkspace()
    {
        var updated = SessionCatalog.WorkspacePath(_chat.Header.Cwd);
        if (string.Equals(CurrentWorkspace, updated, StringComparison.Ordinal))
            return;
        CurrentWorkspace = updated;
        Workbench.NotifyWorkspaceChanged();
    }

    internal GuiShellContext CreateShellContext()
        => new(
            Workspace.ActiveSession?.Machine ?? _chat.Machine,
            CurrentWorkspace,
            id => Workspace.OpenPage(Workspace.Pages.Available.FirstOrDefault(entry => entry.Id == id)),
            ShowToast,
            _pluginCoordinator.RunAsync);

    internal GuiSessionContext CreateSessionContext()
        => new(
            _chat.Machine,
            _chat.Id.Value,
            CurrentWorkspace,
            _pluginCoordinator.RunAsync,
            SetStatus);

    [RelayCommand]
    private async Task PickWorkspaceAsync()
    {
        if (FilePicker is null)
            return;
        var picked = await FilePicker(false, true);
        if (picked.Count > 0)
            SetDefaultWorkspace(picked[0]);
    }

    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    /**
     * 每个会话各自的未发送草稿(文本 + 附件 + 光标)。
     * 切换会话时先保存当前会话的草稿、再复原目标会话的草稿; 否则上一条会话里写到一半的内容
     * 会跟着用户进入下一条会话, 一不小心就发给了错误的会话。
     */
    private readonly Dictionary<string, ComposerDraft> _drafts = new(StringComparer.Ordinal);

    /** 每个会话上次的阅读位置(像素 + 视口顶部那条的身份): 切回来时按它还原, 而不是一上来就滑到最新。 */
    private readonly Dictionary<string, TranscriptAnchor> _transcriptAnchors = new(StringComparer.Ordinal);

    /** 正在整段重放历史: 这期间的逐条通知没有意义(见 AppendMessage)。 */
    private bool _replaying;

    /** 正在应用的那条事件序号: 每条界面行记下它, 会话被截断时据此删掉尾部消息。 */
    private long _currentSeq;

    /** 视图报告当前阅读位置; 只有视图知道这个量, 这里只负责按会话记住它。 */
    public void ReportTranscriptAnchor(TranscriptAnchor anchor) => _transcriptAnchors[_chat.Id.Value] = anchor;

    private sealed record ComposerDraft(string Text, int Caret, IReadOnlyList<AttachmentViewModel> Attachments);

    /** 当前输入区正在显示哪条会话的草稿; null 表示还没显示过任何会话。 */
    private string? _shownSession;

    [ObservableProperty]
    private bool _hasAttachments;

    [ObservableProperty]
    private AttachmentViewModel? _previewAttachment;

    [ObservableProperty]
    private bool _isPreviewOpen;

    partial void OnPreviewAttachmentChanged(AttachmentViewModel? value) => IsPreviewOpen = value is not null;

    /** 把一张图片写入附件存储并挂到输入区(供 GUI 剪贴板贴图与路径路线共用)。 */
    public void AttachImage(string name, byte[] png, int width, int height, string mediaType = "image/png")
    {
        var store = _ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName, false);
        if (store is null)
            return;
        try
        {
            var reference = store.Put(png, mediaType, width, height, name);
            Attachments.Add(new AttachmentViewModel(reference, png));
            HasAttachments = Attachments.Count > 0;
        }
        catch (ArgumentException)
        {
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentViewModel attachment)
    {
        Attachments.Remove(attachment);
        HasAttachments = Attachments.Count > 0;
        if (ReferenceEquals(PreviewAttachment, attachment))
            PreviewAttachment = null;
    }

    [RelayCommand]
    private void OpenPreview(AttachmentViewModel attachment) => PreviewAttachment = attachment;

    [RelayCommand]
    private void ClosePreview() => PreviewAttachment = null;

    [RelayCommand]
    private void NewSession() => _ = NewSessionAsync();

    [RelayCommand]
    private async Task SubmitAsync()
    {
        var text = Composer.Input.Trim();
        // 先抽出消息里的本地图片路径转成附件(粘贴图片后常紧跟指令, 不能只看整段是否是路径)。
        if (text.Length > 0)
        {
            text = await ExtractImagePathsAsync(text);
            Composer.Input = text;
        }
        if (text.StartsWith('/'))
        {
            Composer.Input = "";
            CloseSuggestions();
            await RunCommandAsync(text);
            return;
        }
        if (text.Length == 0 && Attachments.Count == 0)
            return;
        var images = Attachments.Select(attachment => new RemoteImageBlock(
            attachment.Reference.MediaType, attachment.Reference.Width, attachment.Reference.Height,
            attachment.Name, Convert.ToBase64String(attachment.Png))).ToList();
        try
        {
            // The Host event stream is the only source of user-message rendering.
            await _chat.SendAsync(ExpandMentions(text), images);
            Composer.Input = "";
            CloseSuggestions();
            Attachments.Clear();
            HasAttachments = false;
        }
        catch (Exception error)
        {
            StatusText = $"发送失败: {error.Message}";
        }
    }

    /** 抽出文本里可读的本地图片路径挂为附件、从文本移除, 返回剩余文本(通常是用户指令)。 */
    private async Task<string> ExtractImagePathsAsync(string text)
    {
        if (_chat.Machine != MachineSpec.LocalName)
        {
            var client = await _chat.ClientAsync();
            var style = RemotePaths.StyleFor(_chat.Fleet.Find(_chat.Machine)?.Info?.Platform ?? "");
            var (remoteText, images) = await PromptImages.ExtractRemoteAsync(
                client.Files, _chat.Header.Cwd ?? "", style, text, CancellationToken.None);
            foreach (var image in images)
            {
                AttachImage(
                    image.Name ?? "image",
                    Convert.FromBase64String(image.Base64),
                    image.Width,
                    image.Height,
                    image.MediaType);
            }
            return remoteText;
        }
        var remaining = text;
        var cwd = _chat.Header.Cwd ?? Environment.CurrentDirectory;
        foreach (Match match in PromptImages.PathPattern().Matches(text))
        {
            var relative = match.Groups["m"].Success;
            var path = relative ? match.Groups["m"].Value : match.Groups["p"].Value;
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    path = new Uri(path).LocalPath;
                }
                catch (UriFormatException)
                {
                    continue;
                }
            }
            path = path.Trim('"', '\'');
            if (relative && !Path.IsPathRooted(path))
                path = Path.GetFullPath(Path.Combine(cwd, path));
            if (!File.Exists(path) || ImageAttachments.MediaTypeForExtension(Path.GetExtension(path)) is not { } mediaType)
                continue;
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                AttachImage(Path.GetFileName(path), bytes, 0, 0, mediaType);
                remaining = remaining.Replace(match.Value, " ", StringComparison.Ordinal);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return Regex.Replace(remaining.Trim(), " {2,}", " ");
    }

    /**
     * 停止: 发出中断后**本机立刻复位**, 再用宿主的状态确认。
     *
     * 以前这里只有一句 `await InterruptAsync`: 宿主没回、回得晚、或者这一轮早就结束了(idle 相位的中断是空操作),
     * 界面就一直停在"停止"形态, 用户只能看着它卡住 —— 也分不清是"还在跑"还是"已经死了"。
     */
    [RelayCommand]
    private async Task CancelTaskAsync()
    {
        var interrupted = false;
        try
        {
            await (await _chat.ClientAsync()).InterruptAsync(_chat.Id.Value);
            interrupted = true;
        }
        catch (Exception error)
        {
            _ctx.LoggerFor("gui").Warn($"cancel: interrupt failed: {error.Message}");
            StatusText = $"取消未被宿主确认: {error.Message}";
        }
        IsBusy = false;
        _compactionBusy = false;
        _lastEventAt = DateTimeOffset.UtcNow;
        if (!interrupted)
            return;
        StatusText = "已发送取消, 等宿主收尾";
        var sent = DateTimeOffset.UtcNow;
        _cancelSentAt = sent;
        _ = ConfirmCancelAsync(sent);
    }

    /** 取消发出后的一次性确认: 一直没有收尾就说出来, 不让状态行永远停在"等宿主收尾"。 */
    private async Task ConfirmCancelAsync(DateTimeOffset sent)
    {
        await Task.Delay(TimeSpan.FromSeconds(CancelConfirmSeconds));
        if (_disposed || _cancelSentAt != sent)
            return;
        _cancelSentAt = null;
        if (StatusText == "已发送取消, 等宿主收尾")
            StatusText = $"取消未被宿主确认({CancelConfirmSeconds} 秒内没有收尾)";
    }

    [RelayCommand]
    private void ShowChat() => Page = AppPage.Chat;

    [RelayCommand]
    private void ShowDashboard() => Workspace.ShowDashboard();

    /** Escape: closes the quick switcher, else leaves a session, contributed page, settings or the market for the dashboard; the chat page stays. */
    [RelayCommand]
    private void GoBack()
    {
        if (Workspace.Palette.IsOpen)
            Workspace.Palette.CloseCommand.Execute(null);
        else if (Page is not AppPage.Chat and not AppPage.Dashboard)
            Workspace.ShowDashboard();
    }

    [RelayCommand]
    private void ShowWorkspacePage(WorkspacePageEntry? entry) => Workspace.OpenPage(entry);

    [RelayCommand]
    private void ShowSettings() => Page = AppPage.Settings;

    [RelayCommand]
    private void ShowMarket()
    {
        Page = AppPage.Market;
        MarketStatus = "";
        Preferences.RefreshPluginsCommand.Execute(null);
    }

    [RelayCommand]
    private void ShowConversation() => Tab = ChatTab.Conversation;

    [RelayCommand]
    private void ShowTrace() => Tab = ChatTab.Trace;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarVisible = !IsSidebarVisible;
        Gui.Save(Gui.Load() with { SidebarVisible = IsSidebarVisible });
    }

    [RelayCommand]
    private void OpenSession(SessionNodeViewModel? node)
    {
        if (node is null)
            return;
        Page = AppPage.Chat;
        SelectedSession = node;
    }

    [RelayCommand]
    private void BeginRename(SessionNodeViewModel? node)
    {
        if (node is null)
            return;
        node.RenameDraft = node.Title;
        node.IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync(SessionNodeViewModel? node)
    {
        if (node is null || !node.IsRenaming)
            return;
        node.IsRenaming = false;
        var title = node.RenameDraft.Trim();
        if (title.Length == 0 || title == node.Title)
            return;
        try
        {
            await (await _chat.ClientAsync()).SessionEditing.RenameSessionAsync(node.SessionId.Value, title);
        }
        catch (Exception error) when (IsSessionMutationFailure(error))
        {
            StatusText = $"重命名失败: {error.Message}";
            return;
        }
        node.Title = title;
        if (node.SessionId == _chat.Id)
        {
            await _chat.ReloadAsync(forceReset: false);
            SessionTitle = title;
        }
        StatusText = $"已重命名为「{title}」";
        RefreshSessions();
    }

    [RelayCommand]
    private void CancelRename(SessionNodeViewModel? node)
    {
        if (node is not null)
            node.IsRenaming = false;
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionNodeViewModel? node)
    {
        if (node is null)
            return;
        if (node.SessionId == _chat.Id)
        {
            StatusText = "不能删除当前正在使用的会话";
            return;
        }
        try
        {
            await (await _chat.ClientAsync()).SessionEditing.DeleteSessionAsync(node.SessionId.Value);
        }
        catch (Exception error) when (IsSessionMutationFailure(error))
        {
            // 宿主拒绝或掉线只该显示一行状态; 让它从命令里抛出去会带走整个 GUI。
            StatusText = $"删除失败: {error.Message}";
            return;
        }
        // 磁盘上已经没有这条会话, 缓存的目录快照必须失效, 否则它会在侧栏里留下幽灵条目。
        _catalog.InvalidateDiskCache();
        StatusText = "会话已删除";
        RefreshSessions();
    }

    /** 会话改名/删除失败是"宿主拒绝或掉线", 不是"程序写错了": 报给用户, 不能把异常抛到 UI 线程。 */
    private static bool IsSessionMutationFailure(Exception error)
        => error is IOException or StreamJsonRpc.RemoteRpcException or Dsh.Transport.JsonRpcException
            or InvalidOperationException or OperationCanceledException or ObjectDisposedException;

    [RelayCommand]
    private async Task ExportSessionLogAsync()
    {
        try
        {
            var snapshot = await (await _chat.ClientAsync()).GetSessionSnapshotAsync(_chat.Id.Value);
            var events = snapshot.Events;
            var directory = Path.Combine(Home.Root, "exports");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{_chat.Id.Value}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.jsonl");
            await using var stream = File.Create(path);
            await using var writer = new StreamWriter(stream);
            foreach (var sessionEvent in events)
                await writer.WriteLineAsync(DshJson.Serialize(sessionEvent));
            await writer.FlushAsync();
            StatusText = $"已导出 {events.Count} 条事件: {path}";
            CopyRequested?.Invoke(path);
        }
        catch (Exception error)
        {
            StatusText = $"导出失败: {error.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenSessionFolderAsync()
        => StatusText = await DesktopIntegration.OpenPathAsync(Path.Combine(Home.Root, "sessions"));

    [RelayCommand]
    private void SetWorkspaceView(string view) => WorkspaceView = view == GuiSettings.ViewFilesystem ? GuiSettings.ViewFilesystem : GuiSettings.ViewSolution;

    [RelayCommand]
    private void SetSort(string mode)
    {
        SortMode = mode == GuiSettings.SortName ? GuiSettings.SortName : GuiSettings.SortUpdated;
        Gui.Save(Gui.Load() with { SortSessions = SortMode });
        RefreshSessions();
    }

    [RelayCommand]
    private void SelectTraceFilter(TraceFilterViewModel? filter)
    {
        if (filter is null)
            return;
        foreach (var candidate in TraceFilters)
            candidate.IsSelected = ReferenceEquals(candidate, filter);
        Gui.Save(Gui.Load() with { TraceFilter = filter.Kind is { } kind ? TraceItemViewModel.Wire(kind) : GuiSettings.TraceAll });
        RefreshTraceView();
    }

    [RelayCommand]
    private void SelectTraceItem(TraceItemViewModel? item)
    {
        foreach (var trace in TraceItems)
            trace.IsSelected = ReferenceEquals(trace, item);
        if (item?.Message is { } message)
            ScrollRequested?.Invoke(message);
    }

    /**
     * 时间线的三个动作(GUI 入口): 跳转在本地做, 回退/分叉交给注册式会话命令 ,
     * 因为会话的截断与派生必须发生在会话所在的那一侧。
     */
    [RelayCommand]
    private void JumpToMessage(MessageViewModel? message)
    {
        if (message is not null)
            ScrollRequested?.Invoke(message);
    }

    [RelayCommand]
    private Task RevertToMessageAsync(MessageViewModel? message)
        => RunTimelineCommandAsync(message, "revert", revert: true);

    [RelayCommand]
    private Task ForkAtMessageAsync(MessageViewModel? message)
        => RunTimelineCommandAsync(message, "fork", revert: false);

    /**
     * 回退/分叉共用: 服务端把会话截断到"这条消息所属回合之前", 这里再把这条消息的原文放回输入区,
     * 于是用户看到的是"编辑并重发", 而不是一条留在转录里、界面还以为在跑的用户消息。
     *
     * 撤回之后的界面由 session/revert 事件驱动(见 DropTranscriptTail 与 _reverted 区间): 只增量删掉尾部消息,
     * 不再重取整份快照 —— 否则同一件事要走两遍(一遍逐事件重放、一遍分页快照), 观感就是整屏重建两次。
     */
    private async Task RunTimelineCommandAsync(MessageViewModel? message, string action, bool revert)
    {
        if (message?.Seq is not { } seq)
            return;
        var text = message.Text;
        var output = await _chat.RunAsync($"/timestamp {action} #{seq}");
        if (output.Length > 0)
            AppendMessage(new MessageViewModel("系统", output, MessageKind.System, false));
        if (revert)
        {
            // 撤回事件到达时界面会按序号删掉尾部消息(见 DropTranscriptTail), 这里只把原文放回输入区。
            FillComposer(text);
            return;
        }
        // 分叉出的会话只写在磁盘上, host 的会话表里还没有它: 丢掉磁盘快照缓存才看得见,
        // 看见之后直接打开它, 否则用户分叉完还得自己去侧栏找。
        _catalog.InvalidateDiskCache();
        RefreshSessions();
        if (ForkedSessionId(output) is not { } forked || FindSession(SessionId.Create(forked)) is not { } node)
            return;
        await OpenChatAsync(MachineSpec.LocalName, forked);
        SelectedSession = node;
        FillComposer(text);
    }

    /** 把文本放回输入框并把光标放到末尾: 回退/分叉之后用户接着改的就是这段话。 */
    private void FillComposer(string text)
    {
        if (text.Length == 0)
            return;
        Composer.Input = text;
        Composer.CaretIndex = text.Length;
    }

    /** 分叉命令的结果形如 "forked -> 会话 id: ..."; 取出新会话 id 用来打开它。 */
    private static string? ForkedSessionId(string output)
    {
        const string marker = "forked -> ";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var rest = output[(start + marker.Length)..];
        var end = rest.IndexOfAny([' ', '\r', '\n', ':']);
        return end < 0 ? rest : rest[..end];
    }

    [RelayCommand]
    private void ToggleMessageFold(MessageViewModel? message)
    {
        message?.ToggleFoldCommand.Execute(null);
        if (message is { IsSubagentTool: true, IsExpanded: true })
            PopulateSubagentCards();
    }

    /** 打开子代理卡片对应的只读视图 (卡片已关联到具体子会话时)。 */
    [RelayCommand]
    private void OpenSubagentView(MessageViewModel? message)
    {
        if (message?.SubagentSessionId is not { } id)
            return;
        Subagents.OpenCommand.Execute(Subagents.Nodes.FirstOrDefault(node => node.Id == id));
    }

    [RelayCommand]
    private async Task OpenDeliverable(DeliverableItemViewModel? item)
    {
        if (item is null)
            return;
        StatusText = File.Exists(item.Path)
            ? await DesktopIntegration.OpenPathAsync(item.Path)
            : $"文件已不存在: {item.Path}";
    }

    [RelayCommand]
    private void CopyDeliverablePath(DeliverableItemViewModel? item)
    {
        if (item is null)
            return;
        CopyRequested?.Invoke(item.Path);
        StatusText = $"已复制路径: {item.Path}";
    }

    [RelayCommand]
    private void CopyMessage(MessageViewModel? message)
    {
        if (message is { } value)
            CopyRequested?.Invoke(value.Text);
    }

    [RelayCommand]
    private void LikeMessage(MessageViewModel? message)
    {
        if (message is null)
            return;
        message.Feedback = message.Feedback == MessageFeedback.Liked ? MessageFeedback.None : MessageFeedback.Liked;
        StatusText = message.Feedback == MessageFeedback.Liked ? "已记录「有帮助」" : "已取消反馈";
    }

    [RelayCommand]
    private void DislikeMessage(MessageViewModel? message)
    {
        if (message is null)
            return;
        message.Feedback = message.Feedback == MessageFeedback.Disliked ? MessageFeedback.None : MessageFeedback.Disliked;
        StatusText = message.Feedback == MessageFeedback.Disliked ? "已记录「没帮助」" : "已取消反馈";
    }

    [RelayCommand]
    private async Task RegenerateMessageAsync(MessageViewModel? message)
    {
        var lastUser = _lastUserMessages.LastOrDefault();
        if (lastUser is null)
        {
            StatusText = "没有可重新生成的用户消息";
            return;
        }
        StatusText = "重新生成上一条回答";
        await _chat.SendAsync(lastUser.Text);
    }

    [RelayCommand]
    private void InsertMention(string text)
    {
        var insert = $"@{text}";
        Composer.Input = Composer.Input.Length == 0 ? $"{insert} " : $"{Composer.Input.TrimEnd()} {insert} ";
        Composer.CaretIndex = Composer.Input.Length;
    }

    [RelayCommand]
    private async Task AttachFileAsync()
    {
        var files = await PickAsync(allowMultiple: true, folders: false);
        foreach (var file in files)
            InsertMention(Relative(file));
        StatusText = files.Count > 0 ? $"已引用 {files.Count} 个文件" : StatusText;
    }

    [RelayCommand]
    private async Task AttachFolderAsync()
    {
        var folders = await PickAsync(allowMultiple: false, folders: true);
        foreach (var folder in folders)
            InsertMention(Relative(folder));
        StatusText = folders.Count > 0 ? $"已引用文件夹 {folders[0]}" : StatusText;
    }

    [RelayCommand]
    private async Task CompactAsync() => StatusText = await _chat.RunAsync("/compact");

    [RelayCommand]
    private async Task ReloadMcpAsync() => StatusText = await _chat.RunAsync("/mcp");

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        Preferences.Reload();
        StatusText = $"模型列表已刷新（{Preferences.Models.Count} 个）";
        await Task.CompletedTask;
    }


    [RelayCommand]
    private async Task SwitchModelAsync(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return;
        var previousModel = Composer.ModelLabel;
        var previousSubtitle = SessionSubtitle;
        // 先改标签再等 Host: 点击到界面反馈之间没有网络往返, 失败时回滚。
        Composer.ModelLabel = model;
        SessionSubtitle = model;
        try
        {
            var result = await _chat.ExecuteAsync($"/model {model}");
            StatusText = result.Text ?? "";
            if (result.Success)
                RefreshReasoningEfforts();
            else
            {
                Composer.ModelLabel = previousModel;
                SessionSubtitle = previousSubtitle;
            }
        }
        catch
        {
            Composer.ModelLabel = previousModel;
            SessionSubtitle = previousSubtitle;
            throw;
        }
    }

    [RelayCommand]
    private async Task SwitchReasoningAsync(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return;
        var previous = Composer.ReasoningLabel;
        Composer.ReasoningLabel = effort;
        try
        {
            var result = await _chat.ExecuteAsync($"/reasoning {effort}");
            StatusText = result.Text ?? "";
            if (!result.Success)
                Composer.ReasoningLabel = previous;
        }
        catch
        {
            Composer.ReasoningLabel = previous;
            throw;
        }
    }

    [RelayCommand]
    private void MoveSuggestion(int delta)
    {
        if (Suggestions.Count == 0)
            return;
        SuggestionIndex = (SuggestionIndex + delta + Suggestions.Count) % Suggestions.Count;
    }

    [RelayCommand]
    private void ConfirmSuggestion(SuggestionViewModel? suggestion)
    {
        if (suggestion is not null)
        {
            ApplySuggestion(suggestion);
            return;
        }
        if (!IsSuggestionOpen || Suggestions.Count == 0)
            return;
        ApplySuggestion(Suggestions[Math.Clamp(SuggestionIndex, 0, Suggestions.Count - 1)]);
    }

    [RelayCommand]
    private void CloseSuggestions()
    {
        IsSuggestionOpen = false;
        Suggestions.Clear();
    }

    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        Workbench.DetachViewModel(this);
        if (_ownsWorkbench)
            Workbench.Dispose();
        _activityTimer.Stop();
        _subagentWatch.Stop();
        Subagents.Changed -= SyncSubagentWatch;
        _suggestionCancellation?.Cancel();
        _suggestionCancellation?.Dispose();
        _suggestionCancellation = null;
        await _editorTerminalHost.DisposeAsync();
        await _editorClientBinding.DisposeAsync();
        Preferences.BeginNetworkShutdown();
        Workspace.Machines.BeginNetworkShutdown();
        foreach (var dialog in _pendingDialogs.Values)
            dialog?.Invalidate();
        _pendingDialogs.Clear();
        _chat.Fleet.Changed -= OnFleetChanged;
        _chat.Reset -= ShowSession;

        _chat.Received -= OnChatReceived;
        _chat.Filled -= ApplyFilled;
        _chat.Failed -= SetStatus;
        _contentSearch?.Cancel();
        _contentSearch?.Dispose();
        Composer.PropertyChanged -= OnComposerPropertyChanged;
        _settings.Changed -= OnSettingsChanged;
        Subagents.Changed -= PopulateSubagentCards;
        Subagents.Dispose();
        foreach (var unsubscribe in _unsubscribers)
            unsubscribe();
        _unsubscribers.Clear();
        _chat.Dispose();
        try
        {
            await Preferences.WaitForNetworkOperationsAsync();
        }
        finally
        {
            await Workspace.DisposeAsync();
        }
    }

    /** 文件选择由视图提供(Avalonia 对话框需要 TopLevel), 未接视图时返回空。 */
    public Func<bool, bool, Task<IReadOnlyList<string>>>? FilePicker { get; set; }

    public void SetStatus(string text) => StatusText = text;

    public void PersistWindowBounds(double width, double height, int x, int y, bool maximized)
        => Gui.Save(Gui.Load() with
        {
            WindowWidth = width,
            WindowHeight = height,
            WindowX = x,
            WindowY = y,
            WindowMaximized = maximized,
        });

    private async Task<IReadOnlyList<string>> PickAsync(bool allowMultiple, bool folders)
        => FilePicker is null ? [] : await FilePicker(allowMultiple, folders);

    private string Relative(string path)
    {
        var cwd = _chat.Header.Cwd ?? Environment.CurrentDirectory;
        try
        {
            var relative = Path.GetRelativePath(cwd, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
        }
        catch (Exception)
        {
            return path;
        }
    }


    /**
     * 当前请求头随事件流维护, 不再每次回扫整段历史。
     * 重放时节点头会逐条到达, 而 RefreshModelLabels 与每条助手消息都要读它, 回扫会把重放变成 O(n²)。
     */
    private LlmCallConfig? CurrentRequestHeader() => _requestHeader;

    /**
     * 推理档位属于当前会话用的模型: 拿全局默认模型的档位去显示, 换模型后点到的就是别的模型的档位,
     * 会被 /reasoning 拒掉。头部还没落地时退回输入框上的模型标签。
     */
    public void RefreshReasoningEfforts()
    {
        var provider = CurrentRequestHeader()?.Provider;
        var model = CurrentRequestHeader()?.Model;
        var separator = Composer.ModelLabel.IndexOf('/');
        if ((provider is null || model is null) && separator > 0)
        {
            provider = Composer.ModelLabel[..separator];
            model = Composer.ModelLabel[(separator + 1)..];
        }
        Preferences.RefreshReasoningEfforts(provider, model);
    }

    private LlmCallConfig? _requestHeader;

    private IReadOnlyList<SessionNodeViewModel> AllSessions()
        => [.. Workspaces.SelectMany(workspace => workspace.Sessions).Concat(FileSystemNodes.SelectMany(node => node.Sessions))];

    private SessionNodeViewModel? FindSession(SessionId id)
        => AllSessions().FirstOrDefault(node => node.SessionId == id);

    private IReadOnlyList<SessionNodeViewModel> FilteredCatalog(IReadOnlyList<SessionNodeViewModel> catalog)
    {
        var nodes = catalog;
        var search = SearchText.Trim();
        if (search.Length > 0)
            nodes = [.. nodes.Where(node => IsTitleHit(node, search) || _contentMatches.ContainsKey(node.SessionId.Value))];
        if (OnlyWithSessions)
            nodes = [.. nodes.Where(node => node.IsLive)];
        foreach (var node in nodes)
            node.MatchSnippet = _contentMatches.TryGetValue(node.SessionId.Value, out var snippet) ? snippet : "";
        return nodes;
    }

    private IReadOnlyList<WorkspaceGroupViewModel> GroupByWorkspace(IReadOnlyList<SessionNodeViewModel> nodes)
        => [.. nodes
            .GroupBy(node => node.WorkspacePath, StringComparer.Ordinal)
            .OrderByDescending(group => group.Max(node => node.CreatedAt))
            .Select(group =>
            {
                var workspace = new WorkspaceGroupViewModel
                {
                    Name = SessionCatalog.WorkspaceDisplayName(group.Key),
                    Path = group.Key,
                };
                foreach (var node in Sort(group))
                    workspace.Sessions.Add(node);
                return workspace;
            })];

    private IReadOnlyList<WorkspaceNodeViewModel> BuildFileTree(IReadOnlyList<SessionNodeViewModel> nodes)
    {
        var roots = new List<WorkspaceNodeViewModel>();
        var index = new Dictionary<string, WorkspaceNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var path = string.IsNullOrWhiteSpace(node.Cwd) ? "未指定目录" : Path.GetFullPath(node.Cwd);
            var current = EnsurePath(roots, index, path);
            current.Sessions.Add(node);
        }
        return roots;
    }

    private WorkspaceNodeViewModel EnsurePath(
        List<WorkspaceNodeViewModel> roots,
        Dictionary<string, WorkspaceNodeViewModel> index,
        string path)
    {
        if (index.TryGetValue(path, out var existing))
            return existing;
        var parentPath = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.Length == 0)
            name = path;
        var node = new WorkspaceNodeViewModel { Name = name, FullPath = path };
        index[path] = node;
        if (parentPath is { Length: > 0 } && !string.Equals(parentPath, path, StringComparison.OrdinalIgnoreCase))
            EnsurePath(roots, index, parentPath).Children.Add(node);
        else
            roots.Add(node);
        return node;
    }

    private IEnumerable<SessionNodeViewModel> Sort(IEnumerable<SessionNodeViewModel> nodes)
    {
        var search = SearchText.Trim();
        if (search.Length > 0)
            nodes = nodes.OrderByDescending(node => IsTitleHit(node, search));
        return SortMode == GuiSettings.SortName
            ? nodes.OrderBy(node => node.Title, StringComparer.OrdinalIgnoreCase)
            : nodes.OrderByDescending(node => node.CreatedAt);
    }

    private static bool IsTitleHit(SessionNodeViewModel node, string search)
        => node.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
        || node.Workspace.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void RefreshTraceFilters()
    {
        var current = Gui.Load().TraceFilter;
        TraceFilters.Clear();
        AddTraceFilter(null, "全部", current);
        AddTraceFilter(TraceKind.Turn, "轮次", current);
        AddTraceFilter(TraceKind.Reasoning, "思考", current);
        AddTraceFilter(TraceKind.Tool, "工具", current);
        AddTraceFilter(TraceKind.Context, "上下文", current);
        AddTraceFilter(TraceKind.Approval, "审批", current);
        AddTraceFilter(TraceKind.Command, "命令", current);
    }

    private void AddTraceFilter(TraceKind? kind, string label, string current)
    {
        var wire = kind is { } value ? TraceItemViewModel.Wire(value) : GuiSettings.TraceAll;
        TraceFilters.Add(new TraceFilterViewModel(kind, label) { IsSelected = wire == current });
    }

    private void RefreshTraceView()
    {
        var selected = TraceFilters.FirstOrDefault(filter => filter.IsSelected);
        TraceView.Clear();
        foreach (var item in TraceItems)
        {
            if (selected?.Kind is { } kind && item.Kind != kind)
                continue;
            TraceView.Add(item);
        }
    }

    private void OnSettingsChanged() => Dispatcher.UIThread.Post(RefreshSessions);

    /**
     * 子代理徽标不能只等"宿主会话列表变了": 无头子代理在升档前不进会话列表, 于是它一开跑徽标仍是 0,
     * 要等到下一回合某个把它写进店的时刻才跳出来 —— 用户在这期间无从判断子代理到底起没起来。
     *
     * 看护表在"有 subagent 工具调用在飞, 或还有子代理活着"这段时间里每秒问一次宿主; 两种状态都归零就停表,
     * 空闲时零开销。子代理结束(含后台跑到父回合之后)也由它收敛。
     */
    private void SyncSubagentWatch()
    {
        if (_disposed)
            return;
        var active = _subagentCallsInFlight > 0 || Subagents.LiveCount > 0;
        if (active == _subagentWatch.IsEnabled)
            return;
        if (active)
            _subagentWatch.Start();
        else
            _subagentWatch.Stop();
    }

    private void TickSubagentWatch()
    {
        if (_disposed)
            return;
        Subagents.Refresh();
        SyncSubagentWatch();
    }

    private void OnFleetChanged()
    {
        if (_disposed)
            return;
        var current = _chat.Fleet.Find(_chat.Machine)?.Sessions.FirstOrDefault(item => item.SessionId == _chat.Id.Value);
        if (current is not null)
            SessionTitle = current.Title ?? _chat.Id.Value;
        RefreshSessions();
        Subagents.Refresh();
        SyncSubagentWatch();
        _chat.RefreshConnection();
        _ = _editorClientBinding.UpdateAsync(_chat.Fleet.Find(_chat.Machine), _chat.Id.Value, _chat.Header.Cwd ?? Home.Root);
        RefreshPendingDialogs();
    }

    /** Host broadcasts are authoritative: another client's answer closes this client's outstanding dialog. */
    public void RefreshPendingDialogs()
    {
        var pending = _chat.Fleet.Client.Pending().Where(item => item.Machine == _chat.Machine && item.SessionId == _chat.Id.Value).ToList();
        var present = pending.Select(item => item.RequestId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _pendingDialogs.Keys.Where(id => !present.Contains(id)).ToList())
        {
            var dialog = _pendingDialogs[id];
            _pendingDialogs.Remove(id);
            dialog?.Invalidate();
        }
        if (_disposed || DecisionRequested is null)
            return;
        var supportsCancellation = _chat.Fleet.Find(_chat.Machine)?.Hello?.Capabilities?.Contains(DecisionProtocol.Capability) == true;
        foreach (var item in pending)
        {
            if (_pendingDialogs.TryGetValue(item.RequestId, out var existing))
            {
                existing?.SetCancellationSupported(supportsCancellation);
                continue;
            }
            var decision = item.Approval is { } approval
                ? DecisionViewModel.ForApproval(approval.Tool, approval.Arguments, approval.Reason, item.RequestId,
                    approval.Availability?.AllowOnce ?? true, approval.Availability?.AllowForSession ?? true, approval.Availability?.Reject ?? true)
                : DecisionViewModel.ForQuestion(new AskUserQuestionRequest([.. item.Question!.Questions.Select(PendingDecisionViewModel.ToItem)]), item.RequestId);
            decision.SetCancellationSupported(supportsCancellation);
            _pendingDialogs[item.RequestId] = decision;
            _ = AnswerPendingAsync(item, decision);
        }
    }

    private async Task AnswerPendingAsync(FleetPendingItem item, DecisionViewModel decision)
    {
        try
        {
            var result = await DecisionRequested!(decision);
            if (_disposed || decision.IsSuperseded || !_pendingDialogs.ContainsKey(item.RequestId))
                return;
            var client = await _chat.ClientAsync();
            if (_disposed || decision.IsSuperseded || !_pendingDialogs.ContainsKey(item.RequestId))
                return;
            if (item.Approval is not null && result is ApprovalOutcome outcome)
                await client.RespondApprovalAsync(item.RequestId,
                    outcome is ApprovalOutcome.AllowedOnce or ApprovalOutcome.AllowedForSession,
                    rememberForSession: outcome is ApprovalOutcome.AllowedForSession,
                    cancelled: outcome is ApprovalOutcome.Cancelled);
            else if (result is AskUserQuestionAnswer answer)
                await client.RespondQuestionAsync(item.RequestId,
                    [.. answer.Answers.Select(value => new RemoteQuestionAnswerItem(value.Id, value.Selected, value.Custom))],
                    status: answer.Status, reason: answer.Reason);
            if (_pendingDialogs.ContainsKey(item.RequestId))
                _pendingDialogs[item.RequestId] = null;
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                StatusText = $"决定提交失败: {error.Message}";
                if (_pendingDialogs.Remove(item.RequestId))
                    RefreshPendingDialogs();
            }
        }
    }
    private void OnComposerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ComposerViewModel.CaretIndex))
        {
            RefreshSuggestions();
            return;
        }
        if (args.PropertyName != nameof(ComposerViewModel.Input))
            return;
        RefreshSuggestions();
        if (!Composer.Input.StartsWith('/'))
            _commandSuggestionsLoaded = false;
        else if (!_commandSuggestionsLoaded)
        {
            _commandSuggestionsLoaded = true;
            _ = RefreshCommandSuggestionsAsync();
        }
    }

    private async Task RefreshCommandSuggestionsAsync()
    {
        try
        {
            await _chat.RefreshCommandsAsync();
            RefreshSuggestions();
        }
        catch (Exception error)
        {
            if (!_disposed)
                StatusText = $"命令列表读取失败: {error.Message}";
        }
    }


    private Task SwitchToAsync(SessionNodeViewModel node) => OpenHostChatAsync(node.SessionId.Value);

    private async Task OpenHostChatAsync(string id) => await OpenChatAsync(MachineSpec.LocalName, id);

    private async Task OpenChatAsync(string machine, string id)
    {
        try
        {
            var kind = Workspace.Dashboard.Find(machine, id)?.Kind ?? HostSessionKinds.Harness;
            if (_chat.Machine != machine || _chat.Id.Value != id || _chat.Kind != kind)
                await _chat.SwitchToAsync(machine, id, kind);
            Page = AppPage.Chat;
        }
        catch (Exception error)
        {
            StatusText = $"会话无法加载: {error.Message}";
        }
    }

    private async Task RunCommandAsync(string text)
    {
        var execution = await _pluginCoordinator.ExecuteAsync(text);
        if (!execution.Known)
        {
            await _chat.SendAsync(ExpandMentions(text));
            return;
        }
        if (execution.Text is { Length: > 0 } output)
            AppendMessage(new MessageViewModel("系统", output, MessageKind.System, false));
        if (execution.FollowupPrompt is { Length: > 0 } prompt)
            await _chat.SendAsync(ExpandMentions(prompt));
    }

    private async Task NewSessionAsync()
    {
        // 显式设置的工作区优先于当前会话目录: 设置项的意义就是让用户摆脱"会话永远落在启动目录"
        var cwd = Gui.Load().DefaultWorkspace
            ?? _chat.Header.Cwd
            ?? Environment.CurrentDirectory;
        Page = AppPage.Chat;
        try
        {
            var created = await (await _chat.ClientAsync()).CreateSessionAsync(cwd);
            await _chat.SwitchAsync(created.Id);
            RefreshSessions();
        }
        catch (Exception error)
        {
            StatusText = $"新建会话失败: {error.Message}";
        }
    }

    private void ShowSession()
    {
        _openProbe?.Stop();
        // 从"点击会话"起算: HostChatSession 已经把它读历史的那一段计时留下来了。
        _openProbe = new SessionOpenProbe(_chat.OpenMs);
        // 用户感知的"打开"到视口出现为止; 整段历史铺完另有 firstPaint 记着。
        _openProbe.MarkViewport();
        UpdateCurrentWorkspace();
        SwapComposerDraft(_shownSession, _chat.Id.Value);
        _shownSession = _chat.Id.Value;
        _ = _editorClientBinding.UpdateAsync(_chat.Fleet.Find(_chat.Machine), _chat.Id.Value, _chat.Header.Cwd ?? Home.Root);
        _renderedSeq = 0;
        _renderedAny = false;
        _openTurnStartSeq = null;
        _gaps.Clear();
        _reverted.Clear();
        _displayTurn = 0;
        _currentDisplayTurn = 0;
        // 上一个进程可能留下未闭合的 compaction/start: 重放它会把界面卡在"正在压缩",
        // 而那次压缩早已随进程退出。
        _compactionBusy = false;
        _openAssistant = null;
        _openReasoning = null;
        Messages.Clear();
        TraceItems.Clear();
        TraceView.Clear();
        _lastUserMessages.Clear();
        Deliverables.Reset(canOpen: true);
        _pendingToolCalls.Clear();
        SessionTitle = _chat.Header.Title ?? _chat.Id.Value;
        IsBusy = false;
        // 统计是跨会话累积的, 换会话必须先清零: 紧接着的整段重放会把本会话的量重新算一遍,
        // 否则新建会话会继承上一个会话的数字, 切回旧会话又会把 in/out 重复累加。
        TokenStats.Reset();
        _requestHeader = null;
        // 投影缓存命中: 整段历史不必再解析重放, 直接还原出上次的界面状态, 再按增量补上缓存之后的事件。
        if (_chat.CachedProjection is { } projection)
            RestoreProjection(projection);
        ApplyHistory(_chat.HistoryBatches());
        if (_gaps.Count > 0)
            _ = FillGapsAsync();
        RefreshModelLabels();
        ReportHistoryCompleteness();
        SelectedSession = FindSession(_chat.Id);
        Subagents.SetRoot(_chat.Id);
        SyncSubagentWatch();
        // 时间轴要覆盖整段会话: 宿主扫一遍用户消息, 与客户端已渲染多少无关。后台取, 不占首屏。
        TimelineEntries.Clear();
        OnPropertyChanged(nameof(TimelineTotalEvents));
        _ = LoadTimelineAsync();
        OnPropertyChanged(nameof(CurrentSessionId));
        RefreshPendingDialogs();
        // 回放结束 = 输入区可用; 首屏就是这一帧(内容已经进视图模型, 下一次渲染会把它画出来)。
        _openProbe?.MarkFirstPaint();
        _openProbe?.MarkInteractive();
        // 整段重放已经结束, 现在请视图重新落点: 上次读的那条还在就回到它, 不在就贴最新。
        TranscriptRestoreRequested?.Invoke(TranscriptRestoreAnchor);
        Workbench.NotifySessionChanged();
        // 只有"投影确实到了日志末尾、中途也没有跳号"的这一份才值得存: 少一段的投影存下来,
        // 下次重开就是直接还原成那个少一段的界面(实测 887,072 条的会话因此永远停在 53.8%)。
        // 命中缓存且没有新事件的这一种由 SaveProjectionAsync 自己判掉(见 MainViewModel.Projection)。
        if (_chat.HistoryIncomplete is null && _gaps.Count == 0 && _renderedSeq >= _chat.TotalEventCount)
            _ = SaveProjectionAsync();
        ReportOpenCost();
    }

    /** 上一次打开的可感知代价(首屏/可输入/最长卡顿/收尾); 没打开过时为 null。 */
    public OpenCostResult? LastOpenCost { get; private set; }

    /** 整段会话的用户消息时间线(宿主给的): 时间轴据此在打开瞬间就画满, 不必等历史全部渲染。 */
    public ObservableCollection<TimelineEntryViewModel> TimelineEntries { get; } = [];

    /** 时间轴按 序号/总数 给未渲染的刻度定位。 */
    public long TimelineTotalEvents => _chat.TotalEventCount;

    private async Task LoadTimelineAsync()
    {
        var session = _chat;
        try
        {
            var entries = await session.TimelineAsync().ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(session, _chat))
                return;
            TimelineEntries.Clear();
            foreach (var entry in entries)
                TimelineEntries.Add(new TimelineEntryViewModel(entry.Seq, entry.Time, entry.Preview));
            OnPropertyChanged(nameof(TimelineTotalEvents));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            // 时间轴是辅助视图: 取不到就只按已渲染的消息画刻度, 不打扰用户。
            SetStatus($"时间线读取失败: {error.Message}");
        }
    }

    /** 点到还没进转录的刻度: 让宿主把它取回来并落到位(与 /timestamp jump 同一条路径)。 */
    public async Task JumpToTimelineAsync(long seq)
    {
        try
        {
            await _chat.RunAsync($"/timestamp jump #{seq}").ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SetStatus($"跳转失败: {error.Message}");
        }
    }

    /**
     * 打开会话的一行打点(方案 §6.3): 首屏/可输入/主线程最长阻塞/后台收尾完成。
     *
     * 和宿主那条 `session open:` 分开: 宿主量的是"会话可读", 这里量的是"用户看到、能用、界面没被卡住"。
     * 后台任务(投影缓存写)不计入前三个刻度, 但 `total_ms` 要等它结束 —— 否则下一次打开才发现它拖慢。
     */
    private void ReportOpenCost()
    {
        if (_openProbe is not { } probe)
            return;
        _openProbe = null;
        var id = _chat.Id.Value;
        var events = _chat.TotalEventCount;
        // 首帧那次标记是 Post(Render) 出去的: 快照也要排在同一优先级之后, 否则 firstPaint 永远是 -1。
        Dispatcher.UIThread.Post(() => PublishOpenCost(probe, id, events), DispatcherPriority.Render);
    }

    private void PublishOpenCost(SessionOpenProbe probe, string id, long events)
    {
        // 结构化结果留一份: 日志行给人看, 这份给验收与测试断言(同一个探针, 同一批数字)。
        LastOpenCost = probe.Snapshot(probe.ElapsedMs, events);
        OnPropertyChanged(nameof(LastOpenCost));
        _ = Task.Run(async () =>
        {
            // 后台收尾: 投影缓存保存跑在任务里, 这里等它一小会儿(拿不到就以当前时刻为准)。
            await Task.Delay(1).ConfigureAwait(false);
            var line = probe.Finish(id, probe.ElapsedMs, events);
            if (line.Length > 0)
                _ctx.LoggerFor("gui").Info("%s", line);
        });
    }

    /**
     * 历史没取全时明确说出来: 状态行 + 轨迹里都留一条。
     *
     * 不说的后果是用户以为"这段会话本来只有这么多", 于是把界面的残缺当成数据没了 —— 那比慢、比少一段都难查。
     */
    private void ReportHistoryCompleteness()
    {
        if (_chat.HistoryIncomplete is not { Length: > 0 } reason)
            return;
        StatusText = $"会话历史不完整: {reason}";
        AddTrace(_chat.TotalEventCount, TraceKind.Context, "会话历史不完整", reason);
    }

    /**
     * 换会话时换草稿: 先把上一条会话的输入区内容存起来, 再复原目标会话存过的内容
     * (没存过就是空的)。附件跟着一起换, 否则上一条会话挂的图片会跟着被发到新会话去。
     */
    private void SwapComposerDraft(string? previous, string next)
    {
        if (previous is not null)
            _drafts[previous] = new ComposerDraft(Composer.Input, Composer.CaretIndex, [.. Attachments]);
        var draft = _drafts.GetValueOrDefault(next);
        var text = draft?.Text ?? "";
        Composer.Input = text;
        Composer.CaretIndex = Math.Clamp(draft?.Caret ?? 0, 0, text.Length);
        Attachments.Clear();
        foreach (var attachment in draft?.Attachments ?? [])
            Attachments.Add(attachment);
        HasAttachments = Attachments.Count > 0;
        CloseSuggestions();
    }

    private void RefreshModelLabels()
    {
        var config = CurrentRequestHeader();
        var label = $"{config?.Provider}/{config?.Model}";
        Composer.ModelLabel = label;
        SessionSubtitle = label;
        Composer.ReasoningLabel = config?.ReasoningEffort?.Value ?? "推理";
        RefreshFilteredModels();
        // 打开会话就把档位候选算出来: 端点是后台探测的, 等用户点开浮层才启动的话第一次只能是空的。
        RefreshReasoningEfforts();
    }

    /** 实时事件: 不是重放, 因此压缩状态要按真实生命周期切换。 */
    private void OnChatReceived(IReadOnlyList<SessionEvent> batch)
    {
        _lastEventAt = DateTimeOffset.UtcNow;
        ApplyEvents(batch);
        RefreshActivity();
    }

    /**
     * 丢掉"由 keepSeq 及之后的事件产生"的界面内容: 消息、轨迹、待配对工具调用、交付物都按序号重算。
     *
     * 回退(阶段 4a)在日志里只是一条新事件, 所以界面这边要做的正是这一步 —— 与物理截断共用同一段逻辑,
     * 差别只在"要不要把渲染游标退回去"(回退时游标此刻已经前进到回退事件之后)。
     */
    private void DropTranscriptTail(long keepSeq)
    {
        for (var index = Messages.Count - 1; index >= 0; index--)
        {
            if (Messages[index].RenderSeq >= keepSeq)
                Messages.RemoveAt(index);
        }
        for (var index = TraceItems.Count - 1; index >= 0; index--)
        {
            var trace = TraceItems[index];
            if (trace.Seq < keepSeq)
                continue;
            TraceItems.RemoveAt(index);
            TraceView.Remove(trace);
        }
        _lastUserMessages.RemoveAll(message => message.RenderSeq >= keepSeq);
        _pendingToolCalls.Clear();
        _openAssistant = null;
        _openReasoning = null;
        // 交付物清单由事件派生: 回退之后重建一遍, 否则被回退掉的那次"交付"还留在侧栏里。
        Deliverables.Reset(canOpen: true);
        foreach (var sessionEvent in _chat.DeliverableEvents)
        {
            if (sessionEvent.Seq >= keepSeq)
                continue;
            if (sessionEvent.Data is DeliverablesPresentedPayload)
                Deliverables.Observe(sessionEvent);
        }
    }

    /**
     * 整段历史**按批**进投影: 每批各走一遍与实时事件相同的规则(同顺序、同跳号判据),
     * 交出去的那一批随后被会话放掉, 于是峰值只与一批相关而不是整段。
     *
     * 收尾(把每条消息刷到最终文本 + 通知视图)只在全部批跑完之后做一次 ——
     * 与"一次性喂整段"逐字段等价, 差别只在内存占用。
     */
    private void ApplyHistory(IEnumerable<IReadOnlyList<SessionEvent>> batches)
    {
        try
        {
            foreach (var batch in batches)
            {
                if (!ApplyEventsCore(batch, replay: true))
                    return;
            }
        }
        finally
        {
            // 提前返回也收尾: 否则每条消息的正文停在最后一帧之前, 界面看着就是"没写完"。
            FinishReplay();
        }
    }

    /** replay=true 表示在重放既有历史(切会话/重新加载), 而不是收到了实时事件。 */
    private void ApplyEvents(IReadOnlyList<SessionEvent> batch, bool replay = false)
    {
        if (!ApplyEventsCore(batch, replay) || !replay)
            return;
        FinishReplay();
    }

    /** 逐条投影一批事件; 返回 false 只有两种情况: 已释放, 或者这一批本身不该再往下走。 */
    private bool ApplyEventsCore(IReadOnlyList<SessionEvent> batch, bool replay)
    {
        if (_disposed)
            return false;
        _replaying = replay;
        try
        {
            foreach (var sessionEvent in batch)
            {
                if (sessionEvent.Seq < _renderedSeq)
                    continue;
                // 跳号不是致命的: 记下缺的那一段, 这一条照常投影 —— 同一批里的 turn/end、压缩结束、
                // 后续正文都必须落地, 否则界面就停在"停止按钮按不动、底部不刷新"的半死状态。
                if (_renderedAny && sessionEvent.Seq > _renderedSeq)
                    RecordGap(_renderedSeq, sessionEvent.Seq);
                _renderedAny = true;
                _renderedSeq = sessionEvent.Seq + 1;
                _currentSeq = sessionEvent.Seq;
                ApplyEvent(sessionEvent, replay);
            }
        }
        finally
        {
            _replaying = false;
        }
        return true;
    }

    /** 重放收尾时增量不排定时器(见 MessageViewModel.Append): 这里一次性把每条消息刷到最终文本。 */
    private void FinishReplay()
    {
        foreach (var message in Messages)
            message.FlushPending();
        MessagesChanged?.Invoke();
    }

    /**
     * 事件流里出现跳号: 把缺的区间登记下来并说出来, 而不是整批丢掉重新取整段历史。
     *
     * 整段重取是"把已经渲染好的东西全部作废", 代价随会话长度增长; 而且取不回来的原因通常在宿主一侧,
     * 重取会稳定断在同一处(实测每秒一次、界面停在 53.8%)。所以: 只登记缺口, 尾巴继续流, 补缺按区间来。
     */
    private void RecordGap(long start, long end)
    {
        if (end <= start)
            return;
        AddGap(new SeqRange(start, end));
        var missing = _gaps.Sum(gap => gap.Count);
        _ctx.LoggerFor("gui").Warn(
            $"session event gap: missing #{start}..#{end - 1} ({end - start} events); kept the rest, {missing} missing in total");
        StatusText = $"会话历史缺 {missing} 条(#{start}..#{end - 1})";
        // 重放途中不动索引(历史是一个惰性迭代器, 边迭代边补会把批次的边界挪掉); 等这一遍走完再补。
        if (!_replaying)
            _ = FillGapsAsync();
    }

    /**
     * 按缺口区间一个一个补, 补到的当场插回原位。
     *
     * 只补缺口本身: 一段几十到几千条, 而整段重取是几十万条起 —— 后者正是"界面停在半截还每秒重来一次"的成因。
     * 补不回来的留在 `_gaps` 里等下一次(滚动、重开、下一条实时事件都能再触发), 不升级成致命错误。
     */
    private async Task FillGapsAsync()
    {
        if (_gapFillRunning || _disposed)
            return;
        _gapFillRunning = true;
        try
        {
            foreach (var gap in _gaps.ToArray())
            {
                if (_disposed || _chat.HistoryIncomplete is not null && _chat.Coverage.Count == 0)
                    return;
                try
                {
                    if (!await _chat.FillAsync(gap).ConfigureAwait(true))
                        continue;
                }
                catch (Exception error)
                {
                    if (!_disposed)
                        _ctx.LoggerFor("gui").Warn($"session gap fill #{gap.Start}..#{gap.End - 1} failed: {error.Message}");
                }
            }
        }
        finally
        {
            _gapFillRunning = false;
        }
    }

    /** 缺口按区间补回来之后: 把这段事件按序号插回原位, 并把 `_gaps` 里已经补到的部分扣掉。 */
    private void ApplyFilled(IReadOnlyList<SessionEvent> events)
    {
        if (_disposed)
            return;
        var applied = 0;
        _fillingGap = true;
        try
        {
            foreach (var sessionEvent in events.OrderBy(item => item.Seq))
            {
                if (!IsMissing(sessionEvent.Seq))
                    continue;
                _currentSeq = sessionEvent.Seq;
                ApplyEvent(sessionEvent, replay: true);
                applied += 1;
            }
        }
        finally
        {
            _fillingGap = false;
        }
        if (applied == 0)
            return;
        foreach (var sessionEvent in events)
            ShrinkGap(sessionEvent.Seq);
        FinishReplay();
        StatusText = _gaps.Count == 0 ? "准备就绪" : $"会话历史缺 {_gaps.Sum(gap => gap.Count)} 条";
    }

    private bool IsMissing(long seq) => _gaps.Exists(gap => seq >= gap.Start && seq < gap.End);

    /** 这条序号是否落在被撤回的区间里。 */
    private bool IsReverted(long seq) => _reverted.Exists(range => seq >= range.Start && seq < range.End);

    private void AddRevertedRange(SeqRange range)
    {
        if (range.IsEmpty)
            return;
        for (var index = 0; index < _reverted.Count; index += 1)
        {
            if (_reverted[index].Union(range) is not { } merged)
                continue;
            _reverted[index] = merged;
            return;
        }
        _reverted.Add(range);
    }

    /** 从缺口集合里扣掉一条已补到的序号(必要时把区间劈成两段)。 */
    private void ShrinkGap(long seq)
    {
        for (var index = 0; index < _gaps.Count; index += 1)
        {
            var gap = _gaps[index];
            if (seq < gap.Start || seq >= gap.End)
                continue;
            _gaps.RemoveAt(index);
            if (seq > gap.Start)
                _gaps.Insert(index, new SeqRange(gap.Start, seq));
            if (seq + 1 < gap.End)
                _gaps.Insert(index + (seq > gap.Start ? 1 : 0), new SeqRange(seq + 1, gap.End));
            return;
        }
    }

    /** 相邻/重叠的缺口合并成一段, 免得状态行里列出十几条互不相连的小段。 */
    private void AddGap(SeqRange gap)
    {
        for (var index = 0; index < _gaps.Count; index++)
        {
            if (_gaps[index].Union(gap) is not { } merged)
                continue;
            _gaps[index] = merged;
            while (index + 1 < _gaps.Count && _gaps[index].Union(_gaps[index + 1]) is { } next)
            {
                _gaps[index] = next;
                _gaps.RemoveAt(index + 1);
            }
            return;
        }
        _gaps.Add(gap);
    }

    private void ApplyEvent(SessionEvent sessionEvent, bool replay)
    {
        // 撤回掉的区间无论怎么回到这里(实时、重放、迟到补洞)都不许再渲染: 靠"游标恰好走过"来删是不可靠的。
        if (IsReverted(sessionEvent.Seq))
            return;
        // 补洞回来的事件属于更早的回合: 它们只负责把正文/工具卡片补出来,
        // 不许改忙闲、统计、模型标签这些"当下状态"(那是实时流的事)。
        if (_fillingGap)
        {
            switch (sessionEvent.Data)
            {
                case UserMessagePayload:
                case AssistantMessagePayload:
                case ToolCallPayload:
                case ToolResultPayload:
                case DeliverablesPresentedPayload:
                    break;
                default:
                    return;
            }
        }
        if (sessionEvent.Data is TurnStartPayload or TurnEndPayload)
            foreach (var node in AllSessions().Where(node => node.SessionId == _chat.Id))
                node.IsRunning = sessionEvent.Data is TurnStartPayload;
        // 上下文整理(压缩)的进度由插件经由中立视图给出, 界面层因此不引用该插件。
        if (_maintenance?.Describe(sessionEvent) is { } maintenance)
        {
            ApplyMaintenance(sessionEvent.Seq, maintenance, replay);
            return;
        }
        switch (sessionEvent.Data)
        {
            case TurnStartPayload:
                _openTurnStartSeq = sessionEvent.Seq;
                IsBusy = true;
                // 回合号按"回合起点在日志里的先后"编: payload 里的 turn 只用于 turn/start ↔ turn/end 配对,
                // 两个宿主同时驱动同一会话时它会重复(方案 §8.1), 拿它当编号显示会看到两个"第 3 轮"。
                _displayTurn += 1;
                _currentDisplayTurn = _displayTurn;
                TokenStats.ObserveTurn(_currentDisplayTurn, 0);
                AddTrace(sessionEvent.Seq, TraceKind.Turn, $"第 {_currentDisplayTurn} 轮");
                break;
            case StepStartPayload step:
                TokenStats.ObserveTurn(_currentDisplayTurn, step.Step);
                break;
            case TurnEndPayload turn:
                _openTurnStartSeq = null;
                IsBusy = false;
                _cancelSentAt = null;
                TokenStats.RequestFinished();
                AddTrace(sessionEvent.Seq, TraceKind.Turn, $"第 {_currentDisplayTurn} 轮结束", TurnEndText(turn.Reason));
                ApplyTurnEnd(turn.Reason);
                break;
            case UserMessagePayload user:
                ApplyUserMessage(sessionEvent.Seq, user);
                break;
            case RequestHeaderPayload header:
                _requestHeader = header.Header.Config;
                TokenStats.RequestStarted();
                RefreshModelLabels();
                break;
            case AssistantChunkPayload chunk:
                ApplyChunk(sessionEvent.Seq, chunk.Chunk, replay);
                break;
            case AssistantMessagePayload assistant:
                ApplyAssistant(SessionText.AssistantText(assistant.Message.Content));
                break;
            case ToolCallPayload call:
                ApplyToolCall(sessionEvent.Seq, call);
                break;
            case ToolResultPayload result:
                ApplyToolResult(sessionEvent.Seq, result);
                break;
            case RequestContextPayload context:
                // 请求元数据(provider/model)只在轨迹里出现, 进正文会和运行时上下文注入行重复。
                TokenStats.ObserveRequestContext(context.ContextWindow);
                AddTrace(sessionEvent.Seq, TraceKind.Context, $"请求上下文 · {context.Provider}/{context.Model}", context.SystemPromptUpdate ?? "");
                break;
            case CommandRunPayload run:
                AddTrace(sessionEvent.Seq, TraceKind.Command, $"命令 /{run.Name}", run.Args ?? "");
                break;
            case ApprovalAskedPayload asked:
                AddTrace(sessionEvent.Seq, TraceKind.Approval, $"审批 {asked.ToolName}", ApprovalHints.PrimaryArgument(asked.ToolName, asked.Arguments));
                break;
            case ApprovalGrantedPayload granted:
                AddTrace(sessionEvent.Seq, TraceKind.Approval, $"本会话不再询问 {granted.ToolName}");
                break;
            case ApprovalDecidedPayload decided:
                AddTrace(sessionEvent.Seq, TraceKind.Approval, $"审批结果 {decided.Outcome}");
                break;
            case SettingsMutationAuditPayload mutation:
                AddTrace(sessionEvent.Seq, TraceKind.Approval, $"settings {mutation.Outcome} · {mutation.ToolName}",
                    $"{mutation.Path}\n{mutation.Diff}\nbeforeHash={mutation.BeforeHash}\nafterHash={mutation.AfterHash}");
                if (!replay)
                    SetStatus($"settings {mutation.Outcome}: {mutation.Path}");
                break;
            case DeliverablesPresentedPayload presented:
                Deliverables.Observe(sessionEvent);
                AddTrace(sessionEvent.Seq, TraceKind.Tool, $"交付物 {presented.Files.Count} 个", string.Join('\n', presented.Files.Select(file => file.Path)));
                break;
            // 回退(阶段 4a)是一条普通事件: 日志没变短, 所以界面自己把那段内容收掉。
            case SessionRevertPayload revert:
                DropTranscriptTail(revert.TargetSeq);
                // 回退点之后、回退事件之前的这段内容再也不该出现(重放/补洞都不许把它捡回来)。
                AddRevertedRange(new SeqRange(revert.TargetSeq, sessionEvent.Seq));
                _chat.ForgetProjection();
                // 被收掉的正是"当前这一回合"时不会有 TurnEnd 再来: 不在这里收尾, 界面就一直停在
                // "正在运行"、停止按钮按了也没反应(回退只在回合边界切, 所以判起点即可)。
                if (_openTurnStartSeq >= revert.TargetSeq)
                {
                    _openTurnStartSeq = null;
                    IsBusy = false;
                    _cancelSentAt = null;
                }
                AddTrace(sessionEvent.Seq, TraceKind.Turn, $"回退到 #{revert.TargetSeq} 之前", revert.Reason ?? "");
                MessagesChanged?.Invoke();
                TranscriptRestoreRequested?.Invoke(TranscriptRestoreAnchor);
                break;
        }
    }

    private void ApplyUserMessage(long seq, UserMessagePayload payload)
    {
        var text = UserFacingText.StripSystemReminders(SessionText.ContentText(payload.Message.Content));
        switch (payload.Message.Source)
        {
            case UserMessageSource:
                if (text.Length == 0)
                    break;
                var user = AppendMessage(new MessageViewModel("你", text, MessageKind.User, false) { Seq = seq });
                _lastUserMessages.Add(user);
                break;
            // 压缩检查点的正文已经由 compaction/summary 那一行渲染过, 再当成上下文注入会重复一遍摘要。
            case PluginMessageSource source when _maintenance?.IsCheckpointMessage(source) == true:
                break;
            case PluginMessageSource plugin:
                var label = $"上下文注入 · {plugin.Plugin}";
                var injected = AppendMessage(new MessageViewModel("", label, MessageKind.Context, false));
                injected.Detail = UserFacingText.StripSystemReminders(plugin.Summary ?? text);
                AddTrace(seq, TraceKind.Context, label, injected.Detail);
                break;
            default:
                AppendMessage(new MessageViewModel(payload.Message.Source.Kind, text, MessageKind.System, false));
                break;
        }
    }

    private void ApplyTurnEnd(TurnEndReason reason)
    {
        _openAssistant = null;
        _openReasoning = null;
        if (reason is TurnEndReason.Error error)
            AppendMessage(new MessageViewModel("错误", $"{error.Failure.Code}: {error.Failure.Message}", MessageKind.System, false));
    }

    /** 上下文整理(压缩)的进度落在正文与轨迹里; 只用中立视图, 因此这里不出现压缩插件的类型。 */
    private void ApplyMaintenance(long seq, ContextMaintenanceEvent maintenance, bool replay)
    {
        switch (maintenance)
        {
            // 重放时不当作进行中: 历史里未闭合的开始标记属于早已退出的进程。
            case ContextMaintenanceEvent.Started when !replay:
                BeginMaintenance(seq);
                break;
            case ContextMaintenanceEvent.Started:
                break;
            case ContextMaintenanceEvent.Pruned pruned:
                AddTrace(seq, TraceKind.Context, $"裁剪 {pruned.Items} 条过大的工具结果", $"~{pruned.Tokens} tokens");
                break;
            case ContextMaintenanceEvent.Summarized summarized:
                ApplySummarized(seq, summarized);
                break;
            case ContextMaintenanceEvent.Failed failed:
                EndMaintenance();
                AppendMessage(new MessageViewModel("错误", $"压缩失败: {failed.Error}", MessageKind.System, false));
                AddTrace(seq, TraceKind.Context, "压缩失败", failed.Error);
                break;
            case ContextMaintenanceEvent.Finished:
                EndMaintenance();
                break;
        }
    }

    private void BeginMaintenance(long seq)
    {
        // 自动压缩发生在回合内部: 那种情况忙碌位已经由 turn/start 置上, 结束后不能替回合清掉。
        _compactionBusy = !IsBusy;
        IsBusy = true;
        StatusText = "正在压缩上下文…";
        AppendMessage(new MessageViewModel("压缩", "正在压缩上下文，摘要生成可能需要几分钟…", MessageKind.Context, false));
        AddTrace(seq, TraceKind.Context, "压缩开始");
    }

    private void ApplySummarized(long seq, ContextMaintenanceEvent.Summarized summarized)
    {
        var label = $"上下文已压缩 · 收起 {summarized.Items} 条历史 (~{summarized.Tokens} tokens)";
        AppendMessage(new MessageViewModel("", label, MessageKind.Context, false)).Detail = summarized.Summary;
        AddTrace(seq, TraceKind.Context, label, summarized.Summary);
    }

    private void EndMaintenance()
    {
        if (!_compactionBusy)
            return;
        _compactionBusy = false;
        IsBusy = false;
    }

    private void ApplyChunk(long seq, StreamChunk chunk, bool replay = false)
    {
        switch (chunk)
        {
            case StreamChunk.BlockStart { BlockType: "reasoning" }:
                _openReasoning = null;
                AddTrace(seq, TraceKind.Reasoning, "思考");
                break;
            case StreamChunk.ReasoningDelta delta when delta.Text.Length > 0:
                TokenStats.FirstTokenArrived();
                AppendReasoning(delta.Text, replay);
                break;
            case StreamChunk.BlockStart { BlockType: "text" }:
                _openAssistant = null;
                break;
            case StreamChunk.TextDelta delta when delta.Text.Length > 0:
                TokenStats.FirstTokenArrived();
                AppendAssistant(delta.Text, replay);
                break;
            case StreamChunk.Usage usage:
                TokenStats.ObserveUsage(usage.Value);
                break;
            case StreamChunk.BlockEnd:
                // 保留 _openAssistant: 紧随其后的 assistant/message 会用最终文本覆盖同一条记录, 避免出现重复消息。
                _openReasoning = null;
                break;
        }
    }

    private void ApplyAssistant(string text)
    {
        // 补洞时不接"正在写"的那条气泡: 那个游标属于实时回合, 补回来的是已经写完的旧回复。
        if (!_fillingGap && _openAssistant is not null)
        {
            _openAssistant.SetText(text);
            _openAssistant.IsStreaming = false;
            _openAssistant = null;
            return;
        }
        if (text.Length > 0)
            AppendMessage(new MessageViewModel(AssistantRole(), text, MessageKind.Assistant, false) { Seq = _currentSeq });
    }

    private string AssistantRole()
        => CurrentRequestHeader()?.Model ?? "助手";

    private void ApplyToolCall(long seq, ToolCallPayload call)
    {
        // 补洞不接实时回合的"正在写"气泡: 那属于当下这一轮。
        if (!_fillingGap)
        {
            _openAssistant = null;
            _openReasoning = null;
        }
        var isSubagent = string.Equals(call.Name, ISubagentService.DefaultToolName, StringComparison.Ordinal);
        var label = isSubagent ? ReadDescription(call.Arguments) : null;
        var arguments = ToolPresentation.FormatArguments(call.Arguments);
        var message = AppendMessage(new MessageViewModel(
            "工具",
            isSubagent ? SubagentCardText(label) : call.Name,
            MessageKind.Tool,
            false)
        {
            ToolName = isSubagent ? SubagentCardText(label) : call.Name,
            ArgumentText = arguments,
            Seq = seq,
        });
        message.Detail = arguments;
        _pendingToolCalls[call.CallId] = (call.Name, call.Arguments, message);
        if (isSubagent)
        {
            message.IsSubagentTool = true;
            message.SubagentLabel = label;
            message.IsExpanded = false;
            // 子会话是工具执行里才建的: 开跑的这一刻先问一次, 之后由看护表每秒跟进, 不等下一回合。
            _subagentCallsInFlight += 1;
            Subagents.Refresh();
            SyncSubagentWatch();
        }
        AddTrace(seq, TraceKind.Tool, $"工具 {call.Name}", arguments);
    }

    /** subagent 工具的 description 参数即子会话 descriptor 的 Label, 用它把工具卡片关联到子代理条目。 */
    private static string? ReadDescription(string arguments)
    {
        try
        {
            return JsonNode.Parse(arguments)?["description"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SubagentCardText(string? label)
        => string.IsNullOrWhiteSpace(label) ? "子代理" : $"子代理 · {label}";

    /** 已展开的 subagent 卡片按 Label(同序)关联到子代理条目并内联摘要; 目录刷新或首次展开时调用。 */
    private void PopulateSubagentCards()
    {
        if (_disposed)
            return;
        var claimed = new HashSet<SessionId>();
        foreach (var message in Messages)
        {
            if (message is not { IsSubagentTool: true, IsExpanded: true })
                continue;
            var node = ResolveSubagentNode(message, claimed);
            if (node is null)
                continue;
            claimed.Add(node.Id);
            message.SubagentSessionId = node.Id;
            if (Subagents.FindEvents(node.Id) is { } events)
                message.SetSubagentStream(SubagentTranscript.Summarize(events));
        }
    }

    private SubagentNodeViewModel? ResolveSubagentNode(MessageViewModel message, HashSet<SessionId> claimed)
    {
        if (message.SubagentSessionId is { } bound)
        {
            var existing = Subagents.Nodes.FirstOrDefault(node => node.Id == bound);
            if (existing is not null)
                return existing;
        }
        var label = message.SubagentLabel;
        if (string.IsNullOrEmpty(label))
            return null;
        return Subagents.Nodes.FirstOrDefault(node =>
            !claimed.Contains(node.Id) && string.Equals(node.Label, label, StringComparison.Ordinal));
    }

    private void ApplyToolResult(long seq, ToolResultPayload result)
    {
        var text = ToolPresentation.FormatResult(SessionText.ContentText(result.Message.Content));
        var label = result.Error is null ? "结果" : $"错误 {result.Error.Code}";
        _pendingToolCalls.Remove(result.Message.Block.ToolCallId, out var pending);
        MessageViewModel? message = pending.Message;
        if (message is null)
        {
            message = AppendMessage(new MessageViewModel(label, text, MessageKind.Result, false) { Seq = seq });
            message.Detail = text;
        }
        else
        {
            message.SetToolResult(text, result.Error is not null);
        }
        // 记下来源事件: 缓存里只存卡片正文的预览, 展开时按这条工具结果回头取全文(见 UpgradeCachedDiffAsync)。
        if (result.Error is null && DiffCardExtractor.TryExtract(pending.Name, pending.Arguments, result.Meta, text) is { } card)
            message.SetDiff(card, seq);
        AddTrace(seq, TraceKind.Tool, result.Error is null ? "工具结果" : $"工具失败 {result.Error.Code}", text);
        // 子代理工具返回后徽标要跟着回落; 后台子代理仍在跑的话由看护表继续跟进到它结束。
        if (string.Equals(pending.Name, ISubagentService.DefaultToolName, StringComparison.Ordinal))
        {
            _subagentCallsInFlight = Math.Max(0, _subagentCallsInFlight - 1);
            Subagents.Refresh();
            SyncSubagentWatch();
        }
    }

    private void AppendAssistant(string delta, bool replay = false)
    {
        _openAssistant ??= AppendMessage(new MessageViewModel(AssistantRole(), "", MessageKind.Assistant, true));
        _openAssistant.Append(delta, coalesce: !replay);
    }

    private void AppendReasoning(string delta, bool replay = false)
    {
        _openReasoning ??= AppendMessage(new MessageViewModel("思考", "", MessageKind.Reasoning, true));
        _openReasoning.Append(delta, coalesce: !replay);
    }

    /** 按序号把消息插到它该在的位置: 补洞回来的事件序号小于当前位置, 追加就错序了。 */
    private void InsertBySeq(MessageViewModel message)
    {
        if (message.Seq is not { } seq)
        {
            Messages.Add(message);
            return;
        }
        var index = Messages.Count;
        while (index > 0 && Messages[index - 1].Seq is { } previous && previous > seq)
            index -= 1;
        Messages.Insert(index, message);
    }

    private MessageViewModel AppendMessage(MessageViewModel message)
    {
        message.RenderSeq = _currentSeq;
        message.Updated += _ => MessagesChanged?.Invoke();
        if (_fillingGap)
        {
            // 补洞回来的是更早的内容: 追加到末尾会让它跑到最新消息后面, 必须按序号插回原位。
            InsertBySeq(message);
        }
        else
        {
            Messages.Add(message);
        }
        // 整段重放时逐条通知会把长会话的加载拖成一堆贴底与布局轮次; 重放结束由 ApplyEvents 通知一次。
        if (!_replaying)
            MessagesChanged?.Invoke();
        return message;
    }

    private void AddTrace(long seq, TraceKind kind, string title, string detail = "")
    {
        var trace = new TraceItemViewModel
        {
            Seq = seq,
            Kind = kind,
            Title = title,
            Detail = Preview(detail),
            Time = DateTimeOffset.Now.ToString("HH:mm:ss"),
            Message = Messages.Count > 0 ? Messages[^1] : null,
        };
        TraceItems.Add(trace);
        var selected = TraceFilters.FirstOrDefault(filter => filter.IsSelected);
        if (selected?.Kind is null || selected.Kind == kind)
            TraceView.Add(trace);
    }

    private string ExpandMentions(string text)
        => MentionResolver.ExpandMentions(text, CurrentMentionSessions());

    private IReadOnlyList<MentionSessionInfo> CurrentMentionSessions()
        => [.. _catalog.Load().Select(node => new MentionSessionInfo(node.SessionId.Value, node.Title, null))];

    private void RefreshSuggestions()
    {
        if (_updatingSuggestions || _disposed)
            return;
        _suggestionCancellation?.Cancel();
        _suggestionCancellation?.Dispose();
        _suggestionCancellation = null;
        var input = Composer.Input;
        var caret = Composer.CaretIndex;
        if (IsImagePath(input) || input.StartsWith('/') || !MentionResolver.TryGetActiveToken(input, caret, out var start, out var end))
        {
            ApplySuggestions(BuildSuggestions(input, caret));
            return;
        }
        var cancellation = new CancellationTokenSource();
        _suggestionCancellation = cancellation;
        _ = RefreshMentionSuggestionsAsync(input, caret, start, end, cancellation.Token);
    }

    private async Task RefreshMentionSuggestionsAsync(
        string input,
        int caret,
        int start,
        int end,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(60, cancellationToken);
            var sessions = CurrentMentionSessions();
            var cwd = _chat.Header.Cwd ?? Environment.CurrentDirectory;
            var candidates = await MentionResolver.ResolveCandidatesAsync(
                input[(start + 1)..end], cwd, sessions, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _disposed
                || !string.Equals(Composer.Input, input, StringComparison.Ordinal)
                || Composer.CaretIndex != caret)
                return;
            ApplySuggestions([.. candidates.Select(candidate => new SuggestionViewModel("mention", $"@{candidate}", "", candidate))]);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (!_disposed)
                _ctx.LoggerFor("gui").Warn($"failed to resolve mention suggestions: {error.Message}");
        }
    }

    private void ApplySuggestions(IReadOnlyList<SuggestionViewModel> suggestions)
    {
        _updatingSuggestions = true;
        try
        {
            Suggestions.Clear();
            foreach (var suggestion in suggestions)
                Suggestions.Add(suggestion);
            SuggestionIndex = 0;
            IsSuggestionOpen = Suggestions.Count > 0;
        }
        finally
        {
            _updatingSuggestions = false;
        }
    }

    /** 文本里是否含"图片路径"片段(用于: 有图路径时不弹命令/引用补全)。 */
    private static bool IsImagePath(string text)
    {
        try
        {
            return PromptImages.PathPattern().IsMatch(text);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IReadOnlyList<SuggestionViewModel> BuildSuggestions(string input, int caret)
    {
        if (IsImagePath(input))
            return [];
        if (input.StartsWith('/'))
            return CommandSuggestions(input);
        // 引用补全只在光标处存在未完成的 @token 时出现; token 之后已输入提示词就不再算选文件。
        return MentionResolver.TryGetActiveToken(input, caret, out var start, out var end)
            ? MentionSuggestions(input, start, end)
            : [];
    }

    private IReadOnlyList<SuggestionViewModel> CommandSuggestions(string input)
    {
        var body = input[1..];
        var space = body.IndexOf(' ');
        var name = space < 0 ? body : body[..space];
        if (space >= 0)
        {
            var argument = body[(space + 1)..].TrimEnd();
            return ArgumentSuggestions(name, argument);
        }
        return [.. _chat.Commands
            .Where(descriptor => descriptor.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            .Select(descriptor => new SuggestionViewModel("command", $"/{descriptor.Name}", descriptor.Description, $"/{descriptor.Name} "))];
    }

    private IReadOnlyList<SuggestionViewModel> ArgumentSuggestions(string name, string argument)
        => [.. ArgumentCandidates(name, argument)
            .Where(candidate => candidate.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .Select(candidate => new SuggestionViewModel("argument", $"/{name} {candidate}", "", $"/{name} {candidate} "))];

    /** 「从集合里选一个参数」的命令统一在这里登记候选来源。 */
    private IReadOnlyList<string> ArgumentCandidates(string name, string argument)
        => name switch
        {
            "model" => [.. Preferences.Models],
            "reasoning" => [.. Preferences.ReasoningEfforts],
            "session" => [.. _catalog.Load().Select(node => node.SessionId.Value)],
            "skill" => _skillNames,
            "provider" => ProviderCandidates(argument),
            "plugins" => PluginCandidates(argument),
            "timestamp" => TimestampCandidates(argument),
            _ => [],
        };

    private IReadOnlyList<string> PluginCandidates(string argument)
    {
        var space = argument.IndexOf(' ', StringComparison.Ordinal);
        var action = space < 0 ? argument : argument[..space];
        if (action is not ("remove" or "disable" or "enable" or "reload"))
            return space < 0 ? PluginSubcommands : [];
        var packages = _ctx.Get<HarnessPluginManager>("pluginManager", false)?.PackageNames
            ?? [.. Preferences.Plugins.Select(item => item.Package)];
        return [.. packages.Select(package => $"{action} {package}")];
    }

    /**
     * `/timestamp` 的参数: 先选动作, 再选 `#seq`。
     * 补全器会把参数末尾的空格裁掉, 所以"刚选完动作"(参数就是动作名)和"正在选序号"是同一形态,
     * 这一步直接给序号: 它们以 `jump #` 开头, 补全器的前缀过滤仍能命中刚打完的动作名。
     * 序号只从本会话的用户消息里出, 与命令本身的候选规则(`SessionTimeline`)一致,
     * 免得补全里出现一个执行时会被拒的序号。
     */
    private IReadOnlyList<string> TimestampCandidates(string argument)
    {
        var space = argument.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
            return argument is "jump" or "revert" or "fork"
                ? SeqCandidates(argument)
                : ["list", "jump", "revert", "fork"];
        var action = argument[..space];
        return action is "jump" or "revert" or "fork" ? SeqCandidates(action) : [];
    }

    /**
     * 序号候选只从"已渲染的用户消息"里出, 不再读 `_chat.Events`。
     *
     * `_chat.Events` 在整段投影之后会放掉前缀(见 `ReleaseParsedHistory`), 从它算候选就只剩尾段 ——
     * 而 `_lastUserMessages` 是整份历史走过的轻量索引(每条用户消息一个条目), 覆盖面与命令本身的语义一致。
     */
    private IReadOnlyList<string> SeqCandidates(string action)
        => [.. _lastUserMessages.Select(message => $"{action} #{message.RenderSeq}")];

    private IReadOnlyList<string> ProviderCandidates(string argument)
    {
        if (!argument.StartsWith("remove ", StringComparison.Ordinal))
            return ["add", "list", "remove"];
        return [.. HarnessSettings.Load(Home).Providers.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => $"remove {key}")];
    }

    private async Task LoadSkillNamesAsync()
    {
        var catalog = _ctx.Get<ISkillCatalog>(ISkillCatalog.ServiceName, false);
        if (catalog is null)
            return;
        try
        {
            _skillNames = await catalog.ListNames();
        }
        catch (Exception error)
        {
            _ctx.LoggerFor("gui").Warn($"failed to list skills: {error.Message}");
        }
    }

    private IReadOnlyList<SuggestionViewModel> MentionSuggestions(string input, int start, int end)
    {
        var token = input[(start + 1)..end];
        var cwd = _chat.Header.Cwd ?? Environment.CurrentDirectory;
        return [.. MentionResolver.ResolveCandidates(token, cwd, CurrentMentionSessions())
            .Select(candidate => new SuggestionViewModel("mention", $"@{candidate}", "", candidate))];
    }

    private void ApplySuggestion(SuggestionViewModel suggestion)
    {
        if (suggestion.Kind != "mention")
        {
            Composer.Input = suggestion.InsertText;
            Composer.CaretIndex = Composer.Input.Length;
            CloseSuggestions();
            return;
        }
        // token 可能已被用户改到光标之外, 那种情况只收起菜单, 不再改写文本。
        if (MentionResolver.TryGetActiveToken(Composer.Input, Composer.CaretIndex, out var start, out var end))
        {
            // 只替换光标所在的 @token, token 之后已经输入的提示词必须原样保留。
            var (text, caret) = MentionResolver.ReplaceActiveToken(Composer.Input, start, end, suggestion.InsertText);
            Composer.Input = text;
            Composer.CaretIndex = caret;
        }
        CloseSuggestions();
    }

    private static string Preview(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= TracePreviewChars ? flat : flat[..TracePreviewChars] + "…";
    }

    private static string TurnEndText(TurnEndReason reason) => reason switch
    {
        TurnEndReason.Completed => "完成",
        TurnEndReason.Aborted aborted => $"中止 · {aborted.Reason.Kind}",
        TurnEndReason.Blocked => "被阻止",
        TurnEndReason.MaxTokens => "达到 max tokens",
        TurnEndReason.Interrupted => "被打断",
        TurnEndReason.Error error => $"失败 · {error.Failure.Code}",
        _ => reason.Kind,
    };

}
