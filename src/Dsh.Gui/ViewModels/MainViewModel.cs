using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.SessionQuery;
using Dsh.Subagent;

namespace Dsh.Gui.ViewModels;

public enum AppPage
{
    Chat,
    Settings,
    Market,
}

public enum ChatTab
{
    Conversation,
    Trace,
}

public enum SessionMode
{
    Standard,
    Plan,
    FullAccess,
}

/** 主窗口状态: 会话目录、会话流、轨迹、输入胶囊、页面切换与统一决策入口。 */
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public const int TracePreviewChars = 160;
    private const int ContentSearchDebounceMs = 300;
    private const int ContentSearchLimit = 50;
    private const int MinContentQueryLength = 2;

    private const string PresetEventType = "preset/mode";

    private readonly Context _ctx;
    private readonly AgentRegistry _agents;
    private readonly SettingsFacade _settings;
    private readonly SessionCatalog _catalog;
    private readonly CommandBridge _bridge;
    private readonly DispatcherBridge<SessionEvent> _events;
    private readonly List<Func<bool>> _unsubscribers = [];
    private readonly List<MessageViewModel> _lastUserMessages = [];
    private Session? _renamedSession;
    private IReadOnlyList<string> _skillNames = [];
    private readonly Dictionary<string, string> _contentMatches = new(StringComparer.Ordinal);
    private readonly Dictionary<ToolCallId, (string Name, string Arguments)> _pendingToolCalls = [];
    private CancellationTokenSource? _contentSearch;

    private AgentLoopAgent _agent;
    private long _renderedSeq;
    private MessageViewModel? _openAssistant;
    private MessageViewModel? _openReasoning;
    private bool _disposed;
    private bool _updatingSuggestions;

    public MainViewModel(HarnessApp app, AgentLoopAgent agent)
    {
        _ctx = app.Ctx;
        _agent = agent;
        Home = app.Home;
        _agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        Subagents = new SubagentPanelViewModel(_ctx);
        Subagents.Changed += PopulateSubagentCards;
        _settings = new SettingsFacade(_ctx, app.Home);
        _catalog = new SessionCatalog(_ctx);
        _bridge = new CommandBridge(_ctx);
        Gui = new GuiSettings(app.Home);
        _events = new DispatcherBridge<SessionEvent>(ApplyEvents);
        var snapshot = Gui.Load();
        _workspaceView = snapshot.WorkspaceView;
        _sortMode = snapshot.SortSessions;
        _onlyWithSessions = snapshot.ShowOnlyWithSessions;
        _isSidebarVisible = snapshot.SidebarVisible;
        Preferences = new SettingsViewModel(_ctx, app.Home, () => _agent, _bridge, Gui, _settings);
        Preferences.Models.CollectionChanged += (_, _) => RefreshFilteredModels();
        Preferences.ReasoningEfforts.CollectionChanged += (_, _) => HasReasoningEfforts = Preferences.ReasoningEfforts.Count > 0;
        HasReasoningEfforts = Preferences.ReasoningEfforts.Count > 0;
        RefreshFilteredModels();
        Composer.PropertyChanged += OnComposerPropertyChanged;
        _unsubscribers.Add(_ctx.On<SkillsChangedNotification>(notification => { _ = LoadSkillNamesAsync(); }));
        _ = LoadSkillNamesAsync();
        _unsubscribers.Add(_ctx.On<SessionEventNotification>(notification => OnSessionEvent(notification)));
        _unsubscribers.Add(_ctx.OnWaterfall<ApprovalRequestNotification>(
            (notification, next) => OnApprovalRequest(notification, next),
            new EventOptions { Global = true }));
        _unsubscribers.Add(_ctx.OnWaterfall<UserQuestionsRequestNotification>(
            (notification, next) => OnUserQuestionRequest(notification, next),
            new EventOptions { Global = true }));
        _settings.Changed += OnSettingsChanged;
        RefreshTraceFilters();
        RefreshSessions();
        ShowAgent(agent);
    }

    public HarnessHome Home { get; }

    public Context Context => _ctx;

    public GuiSettings Gui { get; }

    public SettingsViewModel Preferences { get; }

    public SubagentPanelViewModel Subagents { get; }

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

    /** 模型浮层列表: 按 ModelSearchText 子串过滤 Preferences.Models, IsCurrent 跟随当前模型标签。 */
    public ObservableCollection<ModelListItem> FilteredModels { get; } = [];

    public ComposerViewModel Composer { get; } = new();

    public TokenStatsViewModel TokenStats { get; } = new();

    public SettingsFacade Settings => _settings;

    public AgentLoopAgent CurrentAgent => _agent;

    /** 视图响应此事件把某条消息滚入视野。 */
    public event Action<MessageViewModel>? ScrollRequested;

    /** 会话流内容发生变化, 视图据此保持贴底滚动。 */
    public event Action? MessagesChanged;

    /** 当前会话切换时触发, 供视图把依赖项重新指向新 agent。 */
    public event Action<AgentLoopAgent>? AgentChanged;

    /** 统一「需要你决定」窗口由视图实现: 审批与 ask_user_question 都走这里。 */
    public event Func<DecisionViewModel, Task<object?>>? DecisionRequested;

    /** 视图负责把文本写进剪贴板。 */
    public event Action<string>? CopyRequested;

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
    private SessionMode _mode = SessionMode.Standard;

    [ObservableProperty]
    private string _preset = "standard";

    [ObservableProperty]
    private IReadOnlyList<PresetListItem> _presetItems = BuildPresetItems("standard");

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

    public string ModeLabel => Mode switch
    {
        SessionMode.Plan => "计划模式",
        SessionMode.FullAccess => "Full access",
        _ => "标准模式",
    };

    public string PresetLabel => PresetDefinitions.FirstOrDefault(definition => definition.Id == Preset).Label ?? Preset;

    public IReadOnlyList<SettingsNavItemViewModel> SettingsSections => Preferences.Sections;

    partial void OnIsBusyChanged(bool value) => Composer.IsBusy = value;

    partial void OnPageChanged(AppPage value)
    {
        IsChatPage = value == AppPage.Chat;
        IsSettingsPage = value == AppPage.Settings;
        IsMarketPage = value == AppPage.Market;
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

    partial void OnModeChanged(SessionMode value) => OnPropertyChanged(nameof(ModeLabel));

    partial void OnPresetChanged(string value)
    {
        OnPropertyChanged(nameof(PresetLabel));
        PresetItems = BuildPresetItems(value);
    }

    /** preset 四项固定, 标签口径与 Dsh.Presets 的 InteractionPreset 对齐; 未知 id 原样显示。 */
    private static readonly (string Id, string Label)[] PresetDefinitions =
        [("standard", "标准"), ("minimal", "极简"), ("ptc", "PTC"), ("creative", "创造")];

    private static IReadOnlyList<PresetListItem> BuildPresetItems(string current)
        => [.. PresetDefinitions.Select(definition => new PresetListItem(definition.Id, definition.Label, definition.Id == current))];

    partial void OnSelectedSessionChanged(SessionNodeViewModel? value)
    {
        foreach (var node in AllSessions())
            node.IsSelected = ReferenceEquals(node, value);
        if (value is null || _disposed)
            return;
        if (value.Agent is { } live && ReferenceEquals(live, _agent))
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
        var nodes = FilteredCatalog();
        foreach (var group in GroupByWorkspace(nodes))
            Workspaces.Add(group);
        foreach (var node in BuildFileTree(nodes))
            FileSystemNodes.Add(node);
        RecentSessions.Clear();
        foreach (var node in Sort(nodes).Take(20))
            RecentSessions.Add(node);
        SelectedSession = FindSession(_agent.Id);
        RebuildRecentWorkspaces();
        UpdateCurrentWorkspace();
    }

    /** 最近工作区 = 已有会话的 distinct 完整路径, 按最近创建倒序; 选中只改默认目录。 */
    private void RebuildRecentWorkspaces()
    {
        RecentWorkspaces.Clear();
        var paths = _catalog.Load()
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

    /** 只设新会话的默认目录, 不切换当前会话。 */
    private void SetDefaultWorkspace(string path)
    {
        Gui.Save(Gui.Load() with { DefaultWorkspace = path });
        StatusText = $"新会话默认工作区已设为 {path}";
        RefreshSessions();
    }

    private void UpdateCurrentWorkspace()
        => CurrentWorkspace = SessionCatalog.WorkspacePath(_agent.Session.Header.Cwd);

    [RelayCommand]
    private async Task PickWorkspaceAsync()
    {
        if (FilePicker is null)
            return;
        var picked = await FilePicker(false, true);
        if (picked.Count > 0)
            SetDefaultWorkspace(picked[0]);
    }

    [RelayCommand]
    private void NewSession() => _ = NewSessionAsync();

    [RelayCommand]
    private async Task SubmitAsync()
    {
        var text = Composer.Input.Trim();
        if (text.Length == 0)
            return;
        Composer.Input = "";
        CloseSuggestions();
        if (text.StartsWith('/'))
        {
            await RunCommandAsync(text);
            return;
        }
        // 用户消息只由会话事件渲染: 本地回显会在恢复会话/重放事件时变成第二条。
        _agent.Followup(MessageFactory.CreateUserText(ExpandMentions(text)));
    }

    [RelayCommand]
    private void CancelTask() => _agent.Cancel(new AgentCancelCause.User());

    [RelayCommand]
    private void ShowChat() => Page = AppPage.Chat;

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
    private void CommitRename(SessionNodeViewModel? node)
    {
        if (node is null || !node.IsRenaming)
            return;
        node.IsRenaming = false;
        var title = node.RenameDraft.Trim();
        if (title.Length == 0 || title == node.Title)
            return;
        var persistence = _ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false);
        if (persistence is null)
        {
            StatusText = "会话持久化不可用";
            return;
        }
        persistence.Rename(node.SessionId, title);
        node.Agent?.Session.Rename(title);
        if (node.SessionId == _agent.Id)
            SessionTitle = title;
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
        if (ReferenceEquals(node.Agent, _agent) || node.SessionId == _agent.Id)
        {
            StatusText = "不能删除当前正在使用的会话";
            return;
        }
        StatusText = await _bridge.RunAsync(_agent, $"/session delete {node.SessionId.Value}");
        RefreshSessions();
    }

    [RelayCommand]
    private async Task ExportSessionLogAsync()
    {
        var persistence = _ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false);
        if (persistence is null)
        {
            StatusText = "会话持久化不可用";
            return;
        }
        try
        {
            using var handle = persistence.Open(_agent.Id, SessionAccess.Read);
            var events = handle.Read();
            var directory = Path.Combine(Home.Root, "exports");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{_agent.Id.Value}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.jsonl");
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
    private void RegenerateMessage(MessageViewModel? message)
    {
        var lastUser = _lastUserMessages.LastOrDefault();
        if (lastUser is null)
        {
            StatusText = "没有可重新生成的用户消息";
            return;
        }
        StatusText = "重新生成上一条回答";
        _agent.Followup(MessageFactory.CreateUserText(lastUser.Text));
    }

    [RelayCommand]
    private void SetMode(string mode)
    {
        switch (mode)
        {
            case "plan":
                _ = TogglePlanModeAsync();
                return;
            case "full":
                ApplyApprovalPolicy(ApprovalPolicy.Auto, SessionMode.FullAccess, "Full access：需要审批的工具自动放行（黑名单仍然生效）");
                return;
            default:
                ApplyApprovalPolicy(ApprovalPolicy.Ask, SessionMode.Standard, "标准模式：需要审批时弹窗确认");
                return;
        }
    }

    [RelayCommand]
    private void SetPermission(string permission)
    {
        switch (permission)
        {
            case "full":
                SetMode("full");
                return;
            default:
                SetMode("standard");
                return;
        }
    }

    [RelayCommand]
    private void InsertMention(string text)
    {
        var insert = $"@{text}";
        Composer.Input = Composer.Input.Length == 0 ? $"{insert} " : $"{Composer.Input.TrimEnd()} {insert} ";
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
    private async Task CompactAsync() => StatusText = await _bridge.RunAsync(_agent, "/compact");

    [RelayCommand]
    private async Task ReloadMcpAsync() => StatusText = await _bridge.RunAsync(_agent, "/mcp");

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        Preferences.Reload();
        StatusText = $"模型列表已刷新（{Preferences.Models.Count} 个）";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SetPreset(string id)
    {
        var output = await _bridge.RunAsync(_agent, $"/preset {id}");
        StatusText = output;
        RefreshPreset();
    }

    [RelayCommand]
    private async Task SwitchModelAsync(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return;
        StatusText = await _bridge.RunAsync(_agent, $"/model {model}");
        RefreshModelLabels();
        Preferences.RefreshReasoningEfforts();
    }

    [RelayCommand]
    private async Task SwitchReasoningAsync(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return;
        StatusText = await _bridge.RunAsync(_agent, $"/reasoning {effort}");
        RefreshModelLabels();
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _contentSearch?.Cancel();
        _contentSearch?.Dispose();
        Composer.PropertyChanged -= OnComposerPropertyChanged;
        _settings.Changed -= OnSettingsChanged;
        Subagents.Changed -= PopulateSubagentCards;
        Subagents.Dispose();
        foreach (var unsubscribe in _unsubscribers)
            unsubscribe();
        _unsubscribers.Clear();
        if (_renamedSession is not null)
            _renamedSession.Renamed -= OnSessionRenamed;
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
        var cwd = _agent.Session.Header.Cwd ?? Environment.CurrentDirectory;
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

    private AgentOptions CurrentOptions()
        => new(_agent.Options.Provider, _agent.Options.Model, _agent.Options.ReasoningEffort, _agent.Options.MaxTokens);

    private IReadOnlyList<SessionNodeViewModel> AllSessions()
        => [.. Workspaces.SelectMany(workspace => workspace.Sessions).Concat(FileSystemNodes.SelectMany(node => node.Sessions))];

    private SessionNodeViewModel? FindSession(SessionId id)
        => AllSessions().FirstOrDefault(node => node.SessionId == id);

    private IReadOnlyList<SessionNodeViewModel> FilteredCatalog()
    {
        var nodes = _catalog.Load();
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

    private void OnSessionEvent(SessionEventNotification notification)
    {
        if (ReferenceEquals(notification.Session, _agent.Session))
            _events.Enqueue(notification.Event);
        if (notification.Event.Data is TurnStartPayload or TurnEndPayload)
            RefreshRunningState(notification.Session, notification.Event.Data is TurnStartPayload);
    }

    /** 后台会话的回合开始/结束也要反映到侧栏, 否则非当前会话看起来像被挂起。 */
    private void RefreshRunningState(Session session, bool running)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;
            foreach (var node in AllSessions())
            {
                if (node.SessionId == session.Id)
                    node.IsRunning = running;
            }
        });
    }

    /** 会话被自动命名或压缩重命名后, 刷新顶部标题与侧栏节点(原本只在切换/新建时刷新)。 */
    private void OnSessionRenamed(Session session, SessionHeader header)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(session, _agent.Session))
                return;
            SessionTitle = header.Title ?? _agent.Id.Value;
            RefreshSessions();
        });
    }

    private void OnComposerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ComposerViewModel.Input))
            RefreshSuggestions();
    }

    private ValueTask<object?> OnApprovalRequest(ApprovalRequestNotification notification, Func<ValueTask<object?>> next)
    {
        var handler = DecisionRequested;
        if (handler is null)
            return next();
        var request = notification.Request;
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchDecision(() => AskAsync(handler, DecisionViewModel.ForApproval(request), answer));
        return new ValueTask<object?>(answer.Task);
    }

    private ValueTask<object?> OnUserQuestionRequest(UserQuestionsRequestNotification notification, Func<ValueTask<object?>> next)
    {
        var handler = DecisionRequested;
        if (handler is null)
            return next();
        var request = notification.Request;
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchDecision(() => AskAsync(handler, DecisionViewModel.ForQuestion(request), answer));
        return new ValueTask<object?>(answer.Task);
    }

    /** 事件可能来自后台线程: 已在 UI 线程时直接处理, 否则投递过去。 */
    private static void DispatchDecision(Func<Task> ask)
    {
        if (Dispatcher.UIThread.CheckAccess())
            _ = ask();
        else
            Dispatcher.UIThread.Post(() => _ = ask());
    }

    private static async Task AskAsync(
        Func<DecisionViewModel, Task<object?>> handler,
        DecisionViewModel decision,
        TaskCompletionSource<object?> answer)
    {
        try
        {
            answer.TrySetResult(await handler(decision));
        }
        catch (Exception)
        {
            answer.TrySetResult(null);
        }
    }

    private async Task SwitchToAsync(SessionNodeViewModel node)
    {
        var agent = node.Agent ?? _agents.Get(node.SessionId) as AgentLoopAgent ?? await ResumeAsync(node);
        if (agent is null)
            return;
        node.Agent = agent;
        ShowAgent(agent);
    }

    private async Task<AgentLoopAgent?> ResumeAsync(SessionNodeViewModel node)
    {
        try
        {
            var handle = await _agents.Resume(new ResumeAgentOptions(node.SessionId, CurrentOptions()));
            var resumed = (AgentLoopAgent)handle.Agent;
            await resumed.WhenIdle();
            return resumed;
        }
        catch (Exception error)
        {
            StatusText = $"会话无法加载: {error.Message}";
            return null;
        }
    }

    private async Task RunCommandAsync(string text)
    {
        var execution = await _bridge.ExecuteAsync(_agent, text);
        if (execution is null)
        {
            // 首 token 不是已知命令: 用户为自己的输入负责, 整行当普通消息发给模型。
            _agent.Followup(MessageFactory.CreateUserText(ExpandMentions(text)));
            return;
        }

        var output = execution.Result switch
        {
            CommandResult.Success { Text: { } successText } => successText,
            CommandResult.Error error => error.Text,
            _ => "",
        };
        if (output.Length > 0)
            AppendMessage(new MessageViewModel("系统", output, MessageKind.System, false));

        // 命令 + 提示词: 命令带的提示词作为下一步用户消息发出。
        var followup = execution.Result switch
        {
            CommandResult.Success success => success.FollowupPrompt,
            CommandResult.Error error => error.FollowupPrompt,
            _ => null,
        };
        if (followup is { Length: > 0 } prompt)
            _agent.Followup(MessageFactory.CreateUserText(ExpandMentions(prompt)));
    }

    private async Task NewSessionAsync()
    {
        // 显式设置的工作区优先于当前会话目录: 设置项的意义就是让用户摆脱"会话永远落在启动目录"
        var cwd = Gui.Load().DefaultWorkspace
            ?? _agent.Session.Header.Cwd
            ?? Environment.CurrentDirectory;
        var handle = await _agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            CurrentOptions()));
        var created = (AgentLoopAgent)handle.Agent;
        await created.WhenIdle();
        RefreshSessions();
        ShowAgent(created);
    }

    private async Task TogglePlanModeAsync()
    {
        var output = await _bridge.RunAsync(_agent, "/plan");
        var enabled = output.Contains("on", StringComparison.OrdinalIgnoreCase);
        Mode = enabled ? SessionMode.Plan : SessionMode.Standard;
        StatusText = output;
    }

    private void ApplyApprovalPolicy(ApprovalPolicy policy, SessionMode mode, string status)
    {
        var approval = _ctx.Get<ApprovalService>(ApprovalService.ServiceName, false);
        if (approval is null)
        {
            StatusText = "审批服务不可用";
            return;
        }
        approval.SetPolicy(_agent, policy);
        Mode = mode;
        RefreshPermissionLabel();
        StatusText = status;
    }

    private void ShowAgent(AgentLoopAgent agent)
    {
        _agent = agent;
        UpdateCurrentWorkspace();
        _renderedSeq = 0;
        _openAssistant = null;
        _openReasoning = null;
        Messages.Clear();
        TraceItems.Clear();
        TraceView.Clear();
        _lastUserMessages.Clear();
        Page = AppPage.Chat;
        SessionTitle = agent.Session.Header.Title ?? agent.Id.Value;
        if (_renamedSession is not null)
            _renamedSession.Renamed -= OnSessionRenamed;
        _renamedSession = agent.Session;
        agent.Session.Renamed += OnSessionRenamed;
        ApplyEvents(agent.Session.SnapshotEvents());
        IsBusy = agent.Status == AgentStatus.Running;
        RefreshMode();
        RefreshPreset();
        RefreshPermissionLabel();
        SelectedSession = FindSession(agent.Id);
        Subagents.SetRoot(agent.Id);
        AgentChanged?.Invoke(agent);
    }

    private void RefreshMode()
        => Mode = PlanActive(_agent.Session) ? SessionMode.Plan : SessionMode.Standard;

    /** preset 状态持久在会话事件 `preset/mode` 里; payload 类型在 Dsh.Presets 程序集, GUI 只能按事件 Type + 泛化属性读取。 */
    private void RefreshPreset()
    {
        var preset = "standard";
        foreach (var sessionEvent in _agent.Session.SnapshotEvents())
        {
            if (sessionEvent.Type != PresetEventType)
                continue;
            preset = ReadStringProperty(sessionEvent.Data, "Preset") ?? preset;
        }
        Preset = preset;
    }

    /** 计划模式状态持久在会话事件 `plan/mode` 里; 这里只读它, 不复制 PlanMode 插件的判定逻辑。 */
    private static bool PlanActive(Session session)
    {
        var active = false;
        foreach (var sessionEvent in session.SnapshotEvents())
        {
            if (sessionEvent.Type != "plan/mode")
                continue;
            active = ReadBoolProperty(sessionEvent.Data, "Active") ?? active;
        }
        return active;
    }

    private static bool? ReadBoolProperty(SessionEventPayload payload, string name)
        => ReadProperty<bool>(payload, name);

    private static string? ReadStringProperty(SessionEventPayload payload, string name)
        => ReadProperty<string>(payload, name);

    /** payload 经 DshJson(camelCase) 序列化后键名会变形, 故按忽略大小写匹配。 */
    private static T? ReadProperty<T>(SessionEventPayload payload, string name)
    {
        try
        {
            if (JsonSerializer.SerializeToNode(payload, payload.GetType(), DshJson.Options) is not JsonObject json)
                return default;
            foreach (var (key, value) in json)
            {
                if (value is not null && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    return value.GetValue<T>();
            }
            return default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private void RefreshPermissionLabel()
    {
        var approval = _ctx.Get<ApprovalService>(ApprovalService.ServiceName, false);
        var policy = approval?.EffectivePolicy(_agent.Session) ?? ApprovalPolicy.Ask;
        Composer.PermissionLabel = policy switch
        {
            ApprovalPolicy.Never => "Never（自动拒绝）",
            ApprovalPolicy.Auto => "Full access",
            _ => "Ask（每次审批）",
        };
        RefreshModelLabels();
    }

    private void RefreshModelLabels()
    {
        var config = _agent.Session.RequestHeader()?.Config;
        var label = $"{config?.Provider ?? _agent.Options.Provider}/{config?.Model ?? _agent.Options.Model}";
        Composer.ModelLabel = label;
        SessionSubtitle = label;
        Composer.ReasoningLabel = config?.ReasoningEffort?.Value ?? "推理";
        RefreshFilteredModels();
    }

    private void ApplyEvents(IReadOnlyList<SessionEvent> batch)
    {
        if (_disposed)
            return;
        foreach (var sessionEvent in batch)
        {
            if (sessionEvent.Seq < _renderedSeq)
                continue;
            _renderedSeq = sessionEvent.Seq + 1;
            ApplyEvent(sessionEvent);
        }
    }

    private void ApplyEvent(SessionEvent sessionEvent)
    {
        if (sessionEvent.Type == PresetEventType)
            RefreshPreset();
        switch (sessionEvent.Data)
        {
            case TurnStartPayload turn:
                IsBusy = true;
                TokenStats.ObserveTurn(turn.Turn, 0);
                AddTrace(sessionEvent.Seq, TraceKind.Turn, $"第 {turn.Turn} 轮");
                break;
            case StepStartPayload step:
                TokenStats.ObserveTurn(step.Turn, step.Step);
                break;
            case TurnEndPayload turn:
                IsBusy = false;
                TokenStats.RequestFinished();
                AddTrace(sessionEvent.Seq, TraceKind.Turn, $"第 {turn.Turn} 轮结束", TurnEndText(turn.Reason));
                ApplyTurnEnd(turn.Reason);
                break;
            case UserMessagePayload user:
                ApplyUserMessage(sessionEvent.Seq, user);
                break;
            case RequestHeaderPayload:
                TokenStats.RequestStarted();
                RefreshModelLabels();
                break;
            case AssistantChunkPayload chunk:
                ApplyChunk(sessionEvent.Seq, chunk.Chunk);
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
            case ApprovalPolicyPayload:
                RefreshPermissionLabel();
                break;
        }
    }

    private void ApplyUserMessage(long seq, UserMessagePayload payload)
    {
        var text = SessionText.ContentText(payload.Message.Content);
        switch (payload.Message.Source)
        {
            case UserMessageSource:
                var user = AppendMessage(new MessageViewModel("你", text, MessageKind.User, false));
                _lastUserMessages.Add(user);
                break;
            case PluginMessageSource plugin:
                var label = $"上下文注入 · {plugin.Plugin}";
                var injected = AppendMessage(new MessageViewModel("", label, MessageKind.Context, false));
                injected.Detail = plugin.Summary ?? text;
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

    private void ApplyChunk(long seq, StreamChunk chunk)
    {
        switch (chunk)
        {
            case StreamChunk.BlockStart { BlockType: "reasoning" }:
                _openReasoning = null;
                AddTrace(seq, TraceKind.Reasoning, "思考");
                break;
            case StreamChunk.ReasoningDelta delta when delta.Text.Length > 0:
                TokenStats.FirstTokenArrived();
                AppendReasoning(delta.Text);
                break;
            case StreamChunk.BlockStart { BlockType: "text" }:
                _openAssistant = null;
                break;
            case StreamChunk.TextDelta delta when delta.Text.Length > 0:
                TokenStats.FirstTokenArrived();
                AppendAssistant(delta.Text);
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
        if (_openAssistant is not null)
        {
            _openAssistant.SetText(text);
            _openAssistant.IsStreaming = false;
            _openAssistant = null;
            return;
        }
        if (text.Length > 0)
            AppendMessage(new MessageViewModel(AssistantRole(), text, MessageKind.Assistant, false));
    }

    private string AssistantRole()
        => _agent.Session.RequestHeader()?.Config.Model ?? _agent.Options.Model ?? "助手";

    private void ApplyToolCall(long seq, ToolCallPayload call)
    {
        _openAssistant = null;
        _openReasoning = null;
        var isSubagent = string.Equals(call.Name, SubagentTool.DefaultToolName, StringComparison.Ordinal);
        var label = isSubagent ? ReadDescription(call.Arguments) : null;
        var message = AppendMessage(new MessageViewModel(
            "工具",
            isSubagent ? SubagentCardText(label) : $"{call.Name} {call.Arguments}",
            MessageKind.Tool,
            false));
        message.Detail = call.Arguments;
        _pendingToolCalls[call.CallId] = (call.Name, call.Arguments);
        if (isSubagent)
        {
            message.IsSubagentTool = true;
            message.SubagentLabel = label;
            message.IsExpanded = false;
        }
        AddTrace(seq, TraceKind.Tool, $"工具 {call.Name}", call.Arguments);
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
            if (Subagents.FindSession(node.Id) is { } session)
                message.SetSubagentStream(SubagentTranscript.Summarize(session));
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
        var text = SessionText.ContentText(result.Message.Content);
        var label = result.Error is null ? "结果" : $"错误 {result.Error.Code}";
        var message = AppendMessage(new MessageViewModel(label, text, MessageKind.Result, false));
        message.Detail = text;
        _pendingToolCalls.Remove(result.Message.Block.ToolCallId, out var pending);
        if (result.Error is null && DiffCardExtractor.TryExtract(pending.Name, pending.Arguments, result.Meta, text) is { } card)
            message.SetDiff(card);
        AddTrace(seq, TraceKind.Tool, result.Error is null ? "工具结果" : $"工具失败 {result.Error.Code}", text);
    }

    private void AppendAssistant(string delta)
    {
        _openAssistant ??= AppendMessage(new MessageViewModel(AssistantRole(), "", MessageKind.Assistant, true));
        _openAssistant.Append(delta);
    }

    private void AppendReasoning(string delta)
    {
        _openReasoning ??= AppendMessage(new MessageViewModel("思考", "", MessageKind.Reasoning, true));
        _openReasoning.Append(delta);
    }

    private MessageViewModel AppendMessage(MessageViewModel message)
    {
        message.Updated += _ => MessagesChanged?.Invoke();
        Messages.Add(message);
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
        => MentionResolver.ExpandMentions(text, _agent.Session.Header.Cwd ?? Environment.CurrentDirectory, CurrentMentionSessions());

    private IReadOnlyList<MentionSessionInfo> CurrentMentionSessions()
        => [.. _catalog.Load().Select(node => new MentionSessionInfo(node.SessionId.Value, node.Title, null))];

    private void RefreshSuggestions()
    {
        if (_updatingSuggestions || _disposed)
            return;
        var input = Composer.Input;
        var suggestions = BuildSuggestions(input);
        _updatingSuggestions = true;
        Suggestions.Clear();
        foreach (var suggestion in suggestions)
            Suggestions.Add(suggestion);
        SuggestionIndex = 0;
        IsSuggestionOpen = Suggestions.Count > 0;
        _updatingSuggestions = false;
    }

    private IReadOnlyList<SuggestionViewModel> BuildSuggestions(string input)
    {
        if (input.StartsWith('@') || input.Contains(" @", StringComparison.Ordinal))
            return MentionSuggestions(input);
        if (input.StartsWith('/'))
            return CommandSuggestions(input);
        return [];
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
        return [.. _bridge.List(_agent)
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
            _ => [],
        };

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

    private IReadOnlyList<SuggestionViewModel> MentionSuggestions(string input)
    {
        var at = input.LastIndexOf('@');
        var end = at + 1;
        while (end < input.Length && !char.IsWhiteSpace(input[end]) && input[end] != '@')
            end++;
        var token = input[(at + 1)..end];
        var cwd = _agent.Session.Header.Cwd ?? Environment.CurrentDirectory;
        return [.. MentionResolver.ResolveCandidates(token, cwd, CurrentMentionSessions())
            .Take(20)
            .Select(candidate => new SuggestionViewModel("mention", $"@{candidate}", "", $"@{candidate}"))];
    }

    private void ApplySuggestion(SuggestionViewModel suggestion)
    {
        if (suggestion.Kind == "argument")
        {
            Composer.Input = suggestion.InsertText;
        }
        else if (suggestion.Kind == "mention")
        {
            var input = Composer.Input;
            var at = input.LastIndexOf('@');
            Composer.Input = at < 0 ? input : input[..at] + suggestion.InsertText + " ";
        }
        else
        {
            Composer.Input = suggestion.InsertText;
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
