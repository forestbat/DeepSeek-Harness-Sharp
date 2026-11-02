using Dsh.Runtime;
using Dsh.Runtime.Events;
using System.Globalization;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Ide.History;
using Dsh.GitInspect;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Tui.Services;

namespace Dsh.Tui;

/**
 * TUI 外壳: 持有 pane 集合与二叉分割树, 把键盘/鼠标路由到焦点 pane, 并按 KeyBindingTable 分发快捷键(默认 Ctrl+X 前缀序列)。
 * 单 pane 时绘制路径与历史行为一致 (Main + RightPanel + Input + Status)。
 * Ctrl+X 组合键走 TerminalRawMode 的进程级原始输入捕获, 不经过宿主编辑控件, 因此 Windows 上不会触发"剪切"。
 */
public sealed partial class ChatWindow : IDisposable
{
    private const long ExitConfirmWindowMs = 2000;

    private readonly Context _ctx;
    private readonly HarnessHome _home;
    private readonly object _gate = new();
    private readonly Queue<(ITuiPane Pane, SessionEvent Event)> _pendingEvents = [];
    private readonly Queue<Action> _pendingActions = [];
    private readonly ISessionPersistence? _persistence;
    private readonly SubagentDirectory _subagents;
    private readonly HashSet<SessionId> _liveSubagents = [];
    private readonly Dictionary<int, ITuiPane> _panes = [];
    private readonly List<OverviewItem> _overviewItems = [];
    private readonly List<SubagentNode> _agentItems = [];
    private IReadOnlyList<MentionSessionInfo>? _sessionInfos;
    private IReadOnlyList<string> _skillCandidates = [];
    private readonly Dictionary<string, IdeHistoryMoment> _ideTimelineMoments = new(StringComparer.Ordinal);
    private (SessionId Session, string Target, IReadOnlyList<IdeRestorePreview> Previews)? _ideRestorePreview;
    private SessionId? _ideTimelineSession;
    private IReadOnlyList<string> _providerCatalogCandidates = [];
    private ProviderCatalogSnapshot? _providerCatalog;
    /** Cancelled by Dispose: background work started by this window must not outlive it. */
    private readonly CancellationTokenSource _lifetime = new();
    private Task _providerCatalogRefresh = Task.CompletedTask;
    private IReadOnlyList<string>? _mcpPanelLines;
    private IReadOnlyList<PtyDaemonSessionDto> _daemonPtys = [];
    private PaneNode _paneTree = new PaneLeaf(0);
    private PaneLayout _paneLayout;
    /** 非空 = 当前整屏显示的是某个 pty 的"层"视图(pty 层不与 session/pane 并排显示)。 */
    private ShellPane? _ptyLayer;
    private SplitDivider? _dividerDrag;
    private bool _rightPanelDrag;
    private bool _inputDrag;
    private int? _rightPanelWidth;
    private int? _inputHeight;
    private readonly TuiSettings? _settings;
    private ConsoleRect _paneArea;
    private ConsoleRect _overlayArea;
    private int _focusedPaneId;
    private int _inputPaneId;
    private int _nextPaneId;
    private int _overviewIndex;
    private bool _overviewActive;
    private int _agentsIndex;
    private bool _agentsActive;
    private bool _exitRequested;
    private long? _exitConfirmAt;
    private readonly KeyBindingTable _keys;
    private readonly KeySequenceMatcher _keySequence;
    private readonly Func<bool> _unsubscribe;
    private readonly Func<bool> _approvalSubscription;
    private readonly Func<bool> _questionsSubscription;
    private readonly Func<bool> _skillChangeSubscription;
    private readonly Func<bool> _subagentStartSubscription;
    private readonly Func<bool> _subagentEndSubscription;

    public ChatWindow(Context ctx, AgentLoopAgent agent, HarnessHome home, ISessionPersistence? persistence = null, TuiSettings? settings = null)
    {
        _ctx = ctx;
        MaintenancePresenter = ctx.Get<IContextMaintenancePresenter>(IContextMaintenancePresenter.ServiceName, false);
        _home = home;
        _persistence = persistence;
        _settings = settings;
        if (settings is not null)
            TuiTheme.Load(settings);
        _rightPanelWidth = settings?.SidebarWidth;
        _inputHeight = settings?.InputHeight;
        var (keys, keyWarnings) = KeyBindingTable.Load(settings?.Keys);
        foreach (var warning in keyWarnings)
            ctx.LoggerFor("tui").Warn(warning);
        _keys = keys;
        _keySequence = new KeySequenceMatcher(keys, settings?.KeySequenceTimeoutMs ?? 0);
        _subagents = new SubagentDirectory(ctx);
        LoadSkillCandidates();
        LoadProviderCatalogCandidates();

        _panes[0] = new ChatPane(this, 0, agent);
        _focusedPaneId = 0;
        _inputPaneId = 0;
        _nextPaneId = 1;
        _paneLayout = LayoutEngine.EvaluatePanes(_paneTree, _paneArea);

        _unsubscribe = ctx.On<SessionEventNotification>(notification =>
        {
            var target = FindPaneBySession(notification.Session);
            if (target is null)
                return;
            QueueSessionEvent(target, notification.Event);
        });

        _approvalSubscription = ctx.OnWaterfall<ApprovalRequestNotification>(async (notification, _) =>
        {
            var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = notification.Signal.UnsafeRegister(
                static state => ((TaskCompletionSource<object?>)state!).TrySetResult(ApprovalOutcome.Cancelled), answer);
            QueueAction(() =>
            {
                if (answer.Task.IsCompleted)
                    return;
                var target = FindPaneByAgent(notification.Request.Agent) ?? InputPane;
                target.ShowApprovalPrompt(notification.Request, answer);
            });
            try
            {
                return await answer.Task.ConfigureAwait(false);
            }
            finally
            {
                WakeHook?.Invoke();
            }
        }, new EventOptions { Global = true });

        _questionsSubscription = ctx.OnWaterfall<UserQuestionsRequestNotification>(async (notification, _) =>
        {
            var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = notification.Signal.UnsafeRegister(
                static state => ((TaskCompletionSource<object?>)state!).TrySetCanceled(), answer);
            QueueAction(() =>
            {
                if (answer.Task.IsCompleted)
                    return;
                var requested = notification.Request.Agent;
                var target = (requested is not null ? FindPaneByAgent(requested) : null) ?? InputPane;
                target.ShowQuestionPrompt(notification.Request, answer);
            });
            try
            {
                return await answer.Task.ConfigureAwait(false);
            }
            finally
            {
                WakeHook?.Invoke();
            }
        }, new EventOptions { Global = true });

        _skillChangeSubscription = ctx.On<SkillsChangedNotification>(_ =>
        {
            LoadSkillCandidates();
        }, new EventOptions { Global = true });

        _subagentStartSubscription = ctx.On<SubagentStartNotification>(notification =>
        {
            lock (_gate)
                _liveSubagents.Add(notification.Info.Id);
            QueueAction(RefreshAgentsIfActive);
        }, new EventOptions { Global = true });

        _subagentEndSubscription = ctx.On<SubagentEndNotification>(notification =>
        {
            lock (_gate)
                _liveSubagents.Remove(notification.Info.Id);
            QueueAction(RefreshAgentsIfActive);
        }, new EventOptions { Global = true });
    }

    public bool ExitRequested => _exitRequested;

    /** UI 事件入队时的唤醒回调: 主循环阻塞等输入期间, agent 流式事件经它唤醒重绘。 */
    internal Action? WakeHook { get; set; }

    public int RenderVersion { get; private set; }

    public int CursorScreenX => InputPane.CursorScreenX;

    public int CursorScreenY => InputPane.CursorScreenY;

    internal Context Ctx => _ctx;

    /** 上下文整理的中立呈现接缝; 未安装提供该能力的插件时为 null。 */
    internal IContextMaintenancePresenter? MaintenancePresenter { get; }

    internal HarnessHome Home => _home;

    internal IReadOnlyList<string> SkillCandidates => _skillCandidates;

    /** models.dev 目录里当前有适配器可服务的 provider 名(`/provider add` 的候选)。 */
    internal IReadOnlyList<string> ProviderCatalogCandidates => _providerCatalogCandidates;

    internal SubagentDirectory Subagents => _subagents;

    internal int PaneCount => _panes.Count;

    internal int FocusedPaneId => _focusedPaneId;

    /** 当前是否整屏显示在某个 pty 的"层"视图里(pty 层不与 session/pane 并排)。 */
    internal bool InPtyLayer => _ptyLayer is not null;

    internal int InputPaneId => _inputPaneId;

    internal bool IsFocusedPaneSubagent => FocusedPane is SubagentPane;

    internal SessionId? FocusedSubagentSessionId => (FocusedPane as SubagentPane)?.Session.Id;

    internal int SubagentPaneCount => _panes.Values.OfType<SubagentPane>().Count();

    internal ChatPane PaneById(int id) => (ChatPane)_panes[id];

    internal ChatPane InputPane => (ChatPane)_panes[_inputPaneId];

    private ITuiPane FocusedPane => _panes[_focusedPaneId];
    private ChatPane? ChoicePane => _panes.Values.OfType<ChatPane>().FirstOrDefault(pane => pane.HasChoicePrompt);

    public void DrainUi()
    {
        List<Action> actions = [];
        lock (_gate)
        {
            while (_pendingActions.Count > 0)
                actions.Add(_pendingActions.Dequeue());
        }

        foreach (var action in actions)
            action();
        foreach (var pane in _panes.Values.OfType<ChatPane>())
            if (pane.SynchronizeChoicePrompt())
                RenderVersion++;

        List<(ITuiPane Pane, SessionEvent Event)> sessionEvents = [];
        lock (_gate)
        {
            while (_pendingEvents.Count > 0)
                sessionEvents.Add(_pendingEvents.Dequeue());
        }

        foreach (var (pane, sessionEvent) in sessionEvents)
        {
            if (_panes.TryGetValue(pane.Id, out var current) && ReferenceEquals(current, pane))
                pane.ProcessSessionEvent(sessionEvent);
        }

        if (actions.Count > 0 || sessionEvents.Count > 0)
            RenderVersion++;
    }

    /** 括号粘贴: 交给输入 pane(本地图片路径转附件 / 空粘贴读剪贴板 / 其余插入文本)。 */
    public void HandlePaste(string text)
    {
        RenderVersion++;
        (ChoicePane ?? InputPane).HandlePaste(text);
    }

    /** 切窗格的一组动作: 提示里合写成 `方向键/O`。 */
    internal static readonly TuiAction[] FocusActions =
        [TuiAction.FocusLeft, TuiAction.FocusRight, TuiAction.FocusUp, TuiAction.FocusDown, TuiAction.FocusNextPane];

    /** 前缀等待中的提示里列出的动作(聊天输入焦点)。 */
    private static readonly KeyHint[] ChatPrefixHints =
    [
        new("新会话", TuiAction.NewSession),
        new("会话", TuiAction.SessionMenu),
        new("detach", TuiAction.Detach),
        new("删会话", TuiAction.DeleteSessionMenu),
        new("子代理", TuiAction.AgentList),
        new("总览", TuiAction.Overview),
        new("开 shell", TuiAction.OpenShell),
        new("分屏", TuiAction.SplitPane),
        new("关窗格", TuiAction.ClosePane),
        new("切窗格", FocusActions),
        new("切会话", TuiAction.PrevSession, TuiAction.NextSession),
        new("退出", TuiAction.Quit),
    ];

    /** 前缀等待中的提示里列出的动作(shell 窗格焦点)。 */
    private static readonly KeyHint[] ShellPrefixHints =
    [
        new("新会话", TuiAction.NewSession),
        new("会话", TuiAction.SessionMenu),
        new("detach", TuiAction.Detach),
        new("子代理", TuiAction.AgentList),
        new("总览", TuiAction.Overview),
        new("开 shell", TuiAction.OpenShell),
        new("分屏", TuiAction.SplitPane),
        new("关窗格", TuiAction.ClosePane),
        new("切会话", TuiAction.PrevSession, TuiAction.NextSession),
        new("退出", TuiAction.Quit),
    ];

    /** 当前生效的快捷键表(默认键位叠加 settings.yaml 的 keys 段): 分发与所有提示文本同源。 */
    internal KeyBindingTable KeyBindings => _keys;

    public void HandleKey(ConsoleKeyInfo key)
    {
        RenderVersion++;
        // 无键名也无字符的事件(纯修饰键按下等)不携带任何意图, 放行会误消费等待中的前缀与审批态。
        if (key.Key == ConsoleKey.NoName && key.KeyChar == '\0')
            return;
        if (ChoicePane is { } choice)
        {
            choice.HandleKey(key);
            return;
        }
        if (_overviewActive)
        {
            HandleOverviewKey(key);
            return;
        }

        if (_agentsActive)
        {
            HandleAgentListKey(key);
            return;
        }

        // 焦点在 shell 窗格: 按键直达 PTY(含 Ctrl+C 交给 shell), 只有窗口级动作的按键序列仍归窗口。
        if (FocusedPane is ShellPane shell)
        {
            if (!TryDispatchKey(key, KeyContext.Shell))
                shell.HandleKey(key);
            return;
        }

        if (TryDispatchKey(key, KeyContext.Chat))
            return;
        ClearExitConfirm();
        if (FocusedPane is SubagentPane subagent && subagent.HandleKey(key))
            return;
        InputPane.HandleKey(key);
    }

    /** 前缀等待超时则收起提示; 主循环空闲时调用(超时为 0 时什么也不做)。 */
    public void ExpireKeySequence()
    {
        if (!_keySequence.Expire(Environment.TickCount64))
            return;
        InputPane.RefreshMenuStatus();
        RenderVersion++;
    }

    /** 按键 → 动作: 命中则执行, 前缀则提示后续键; 返回 false 表示这个键与快捷键无关, 由调用方照常处理。 */
    private bool TryDispatchKey(ConsoleKeyInfo key, KeyContext context)
    {
        ExpireKeySequence();
        var match = _keySequence.Feed(KeyChord.From(key), context, Environment.TickCount64);
        switch (match.Kind)
        {
            case KeyMatchKind.None:
                return false;
            case KeyMatchKind.Pending:
                SetFocusedStatus(_keys.PrefixHint(match.Typed, context == KeyContext.Shell ? ShellPrefixHints : ChatPrefixHints));
                return true;
            case KeyMatchKind.Cancelled:
                // 前缀之后接不上的键: 前缀作废、这个键不再输入(与 tmux 的前缀语义一致)。
                if (PrepareForWindowAction(null))
                    InputPane.RefreshMenuStatus();
                return true;
            default:
                RunKeyAction(match.Action, match.IsSequence);
                return true;
        }
    }

    private void RunKeyAction(TuiAction action, bool sequence)
    {
        if (action == TuiAction.Interrupt)
        {
            Interrupt();
            return;
        }

        // 单键动作视为一次普通按键, 打断"再按一次 Ctrl+C 退出"; 序列动作(前缀之后)不打断。
        if (!sequence)
            ClearExitConfirm();
        if (KeyBindingTable.AppliesIn(action, KeyContext.Shell) && !PrepareForWindowAction(action))
            return;
        RunBoundAction(action);
    }

    /**
     * 在 pty 层视图里: 切该 pty 的会话与总览留在这一层, 关窗格就是退出这一层(这一层不是会话里的窗格);
     * 其余动作作用在会话/窗格上, 先退出这一层再执行, 否则会对着不显示的东西下手。
     * 返回 false 表示动作已经被"退出这一层"消化。
     */
    private bool PrepareForWindowAction(TuiAction? action)
    {
        if (_ptyLayer is null)
            return true;
        if (action == TuiAction.ClosePane)
        {
            LeavePtyLayer();
            return false;
        }

        if (action is not (TuiAction.PrevSession or TuiAction.NextSession or TuiAction.Overview))
            LeavePtyLayer();
        return true;
    }

    private void RunBoundAction(TuiAction action)
    {
        var input = InputPane;
        switch (action)
        {
            case TuiAction.NewSession:
                RunSlashCommand(input, "/new");
                return;
            case TuiAction.SessionMenu:
                PrefillCommand(input, "/session ");
                // 终端窗格内打开会话菜单: 把焦点带回 DSH 命令行, 保证 /session 可输入(不打断 shell 进程)。
                FocusPane(input.Id);
                return;
            case TuiAction.Detach:
                RequestDetach(input);
                return;
            case TuiAction.DeleteSessionMenu:
                PrefillCommand(input, "/session delete ");
                return;
            case TuiAction.AgentList:
                OpenAgentList();
                return;
            case TuiAction.Overview:
                OpenOverview();
                return;
            case TuiAction.OpenShell:
                AddShellPane();
                return;
            case TuiAction.Quit:
                RequestExit();
                return;
            case TuiAction.SplitPane:
                SplitFocusedPane();
                return;
            case TuiAction.ClosePane:
                CloseFocusedPane();
                return;
            case TuiAction.PrevSession or TuiAction.NextSession:
                SwitchSessionInPty(action == TuiAction.NextSession ? 1 : -1);
                return;
            case TuiAction.FocusLeft or TuiAction.FocusRight or TuiAction.FocusUp or TuiAction.FocusDown:
                MoveFocus(FocusDirectionOf(action));
                return;
            case TuiAction.FocusNextPane:
                FocusPane(PaneTree.NextPane(_paneLayout, _focusedPaneId));
                return;
            case TuiAction.PromptCommandMenu:
                input.OpenPromptCommandMenu();
                return;
        }
    }

    private static FocusDirection FocusDirectionOf(TuiAction action)
        => action switch
        {
            TuiAction.FocusLeft => FocusDirection.Left,
            TuiAction.FocusRight => FocusDirection.Right,
            TuiAction.FocusUp => FocusDirection.Up,
            _ => FocusDirection.Down,
        };

    private static void PrefillCommand(ChatPane input, string command)
    {
        input.Input = command;
        input.Cursor = input.Input.Length;
        input.RefreshMenus();
    }

    /** 中断键: 回合进行中取消回合; 空闲时连按两次(间隔不超过 ExitConfirmWindowMs)退出。 */
    private void Interrupt()
    {
        var input = InputPane;
        if (input.Busy)
        {
            ClearExitConfirm();
            input.Agent.Cancel(new AgentCancelCause.User());
            return;
        }

        if (_exitConfirmAt is { } firstPress && Environment.TickCount64 - firstPress <= ExitConfirmWindowMs)
        {
            _exitConfirmAt = null;
            RequestExit();
            return;
        }

        _exitConfirmAt = Environment.TickCount64;
        input.StatusText = $"再按一次 {_keys.Keys(TuiAction.Interrupt)} 退出";
    }

    private void SetFocusedStatus(string hint)
    {
        var target = FocusedPane as ShellPane;
        if (target is not null)
            target.StatusText = hint;
        else
            InputPane.StatusText = hint;
    }

    public void InsertText(string text)
    {
        RenderVersion++;
        if (ChoicePane is { } choice)
        {
            choice.InsertText(text);
            return;
        }
        // 焦点在 shell 窗格时粘贴内容直达 PTY(对齐终端行为), 不进 AI 输入行。
        if (FocusedPane is ShellPane shell)
        {
            shell.HandleText(text);
            return;
        }
        InputPane.InsertText(text);
    }

    public void HandleMouseWheel(float delta)
    {
        RenderVersion++;
        if (ChoicePane is { } choice)
        {
            choice.HandleMouseWheel(delta);
            return;
        }
        FocusedPane.HandleMouseWheel(delta);
    }

    /** 鼠标滚轮按指针所在 pane 路由; 无命中时回退焦点 pane。 */
    public void HandleMouseWheel(float delta, int cellX, int cellY, UiLayout layout)
    {
        RenderVersion++;
        if (ChoicePane is { } choice)
        {
            choice.HandleMouseWheel(delta);
            return;
        }
        layout = Effective(layout);
        // pty 层视图: 整屏就是那一层, 滚轮只滚它(不命中窗格树, 也不滚侧栏/浮层)。
        if (_ptyLayer is { } layer)
        {
            layer.HandleMouseWheel(delta);
            return;
        }
        // 命令浮层打开时: 正文区滚轮滚动候选(与 ^/v 等价), 便于浏览长命令列表。
        if (layout.Main.Contains(cellX, cellY) && InputPane.ScrollOverlay(delta))
            return;
        // 指针落在右栏上: 滚右栏内容(窄终端里才看得到下面的段落), 不再滚正文。分屏时右栏同样在正文区之外。
        if (layout.RightPanel.Contains(cellX, cellY) && InputPane.ScrollRightPanel(delta))
            return;
        var target = _panes.Count > 1 ? _paneLayout.HitTest(cellX, cellY) : null;
        if (target is { } id && _panes.TryGetValue(id, out var pane))
            pane.HandleMouseWheel(delta);
        else
            FocusedPane.HandleMouseWheel(delta);
    }

    public void HandleMouseClick(int cellX, int cellY, UiLayout layout)
    {
        RenderVersion++;
        if (ChoicePane is { } choice)
        {
            choice.HandleChoicePointer(cellX, cellY, click: true);
            return;
        }
        layout = Effective(layout);
        // pty 层视图整屏就是那一层: 点击不碰窗格树/分隔线(拖拽也没有意义)。
        if (_ptyLayer is not null)
            return;
        var input = InputPane;
        if (input.TryBeginMentionScrollDrag(cellX, cellY))
            return;
        if (_panes.Count > 1 && FindDividerAt(cellX, cellY) is { } divider)
        {
            _dividerDrag = divider;
            return;
        }

        // 右栏竖线(拖宽度)和输入栏上沿(拖高度)都可拖(分屏时也保留右栏, 所以不限于单窗格)。
        var dividerColumn = RightPanelDividerColumn(layout);
        if (dividerColumn >= 0
            && Math.Abs(cellX - dividerColumn) <= 1
            && cellY >= layout.Main.Y
            && cellY < layout.Main.Bottom)
        {
            _rightPanelDrag = true;
            return;
        }

        if (cellY < layout.Input.Y && cellY >= layout.Input.Y - 2)
        {
            _inputDrag = true;
            return;
        }
        if (layout.Input.Contains(cellX, cellY))
        {
            input.HandleInputClick(cellX, layout.Input);
            return;
        }

        if (_panes.Count == 1)
        {
            if (layout.Main.Contains(cellX, cellY))
            {
                if (FocusedPane.TryToggleFoldAt(cellY, layout.Main))
                    return;
                if (TryResolveTranscript(cellX, cellY, layout, out var pane, out var rect))
                    pane.BeginSelection(cellX, cellY, rect);
            }
            return;
        }

        var hit = _paneLayout.HitTest(cellX, cellY);
        if (hit is null)
            return;
        if (hit.Value != _focusedPaneId)
        {
            FocusPane(hit.Value);
            return;
        }

        if (TryResolveTranscript(cellX, cellY, layout, out var target, out var transcript))
        {
            if (target.TryToggleFoldAt(cellY, transcript))
                return;
            target.BeginSelection(cellX, cellY, transcript);
            return;
        }
        FocusedPane.StickToBottom = false;
    }

    /** Hover belongs only to the active prompt and never extends transcript selection. */
    public void HandleMouseMove(int cellX, int cellY)
    {
        if (ChoicePane is not { } choice)
            return;
        if (choice.HandleChoicePointer(cellX, cellY, click: false))
            RenderVersion++;
    }

    /** Drag a divider or extend transcript selection unless a prompt owns the pointer. */
    public void HandleMouseDrag(int cellX, int cellY, UiLayout layout)
    {
        RenderVersion++;
        if (ChoicePane is { } choice)
        {
            choice.HandleChoicePointer(cellX, cellY, click: false);
            return;
        }
        layout = Effective(layout);
        if (InputPane.DragMentionScrollDrag(cellY))
            return;
        if (_inputDrag)
        {
            DragInputHeight(cellY, layout);
            return;
        }
        if (_rightPanelDrag)
        {
            DragRightPanel(cellX, layout);
            return;
        }
        if (_dividerDrag is { } divider)
        {
            DragDivider(divider, cellX, cellY);
            return;
        }
        if (TryResolveTranscript(cellX, cellY, layout, out var pane, out var rect))
            pane.ExtendSelection(cellX, cellY, rect);
    }

    /** 指针释放: 结束分隔线拖动或文本选择(有选中文本则写入剪贴板)。 */
    public string? HandleMouseRelease(int cellX, int cellY, UiLayout layout)
    {
        RenderVersion++;
        if (ChoicePane is not null)
        {
            CancelPointerGestures();
            return null;
        }
        layout = Effective(layout);
        if (InputPane.EndMentionScrollDrag())
            return null;
        if (_inputDrag)
        {
            _inputDrag = false;
            SaveLayoutSizes();
            return null;
        }
        if (_rightPanelDrag)
        {
            _rightPanelDrag = false;
            SaveLayoutSizes();
            return null;
        }
        if (_dividerDrag is not null)
        {
            _dividerDrag = null;
            return null;
        }
        if (!TryResolveTranscript(cellX, cellY, layout, out var pane, out _))
            return null;
        var text = pane.FinishSelection();
        if (text is not null)
            _ = Clipboard.TrySetTextAsync(text);
        return text;
    }

    internal void CancelPointerGestures()
    {
        _inputDrag = false;
        _rightPanelDrag = false;
        _dividerDrag = null;
        foreach (var pane in _panes.Values)
        {
            if (pane is not ChatPane chat)
                continue;
            chat.EndMentionScrollDrag();
            chat.ClearSelection();
        }
    }

    /** 控制台总列数: 正文宽 + 竖分割线 + 右栏宽(没有右栏就没有竖线)。 */
    private static int ConsoleWidthOf(UiLayout layout)
        => layout.Main.Width + layout.RightPanel.Width + (layout.RightPanel.Width > 0 ? 1 : 0);

    /**
     * 把宿主给的默认布局换成带用户覆盖值(侧栏宽度/输入栏高度)的布局。
     * 绘制、命中测试、拖动都先过这里, 三处共用同一个夹取口径, 不会再出现"线在 A 处、可拖位置在 B 处"。
     */
    private UiLayout Effective(UiLayout layout)
    {
        // 没有用户覆盖值就不要重算: 直接用宿主给的布局, 免得与宿主计算出现任何偏差。
        if (_rightPanelWidth is null && _inputHeight is null)
            return layout;
        var width = Math.Max(layout.Main.Right, layout.RightPanel.Right);
        var height = Math.Max(layout.Status.Bottom, layout.Input.Bottom);
        return LayoutEngine.Calculate(width, height, _rightPanelWidth, _inputHeight);
    }

    /** 单窗格右栏竖分割线所在列; 没有右栏时返回 -1。 */
    private static int RightPanelDividerColumn(UiLayout layout)
        => layout.RightPanel.Width > 0 ? layout.RightPanel.X - 1 : -1;

    /** 拖动侧栏分割线: 指针列换算成右栏宽度(夹取由 LayoutEngine 统一负责)。 */
    private void DragRightPanel(int cellX, UiLayout layout)
        => _rightPanelWidth = LayoutEngine.ClampRightPanelWidth(ConsoleWidthOf(layout), ConsoleWidthOf(layout) - 1 - cellX);

    /** 拖动输入栏上沿: 指针行换算成输入栏高度(夹取由 LayoutEngine 统一负责)。 */
    private void DragInputHeight(int cellY, UiLayout layout)
    {
        var height = Math.Max(layout.Status.Bottom, layout.Input.Bottom);
        // 让输入栏上沿落在指针下一行: Layout 里 Input.Y = 控制台高 - DividerRows(2) - 行数, 故 行数 = 高 - 3 - 指针行。
        _inputHeight = LayoutEngine.ClampInputHeight(height, height - 3 - cellY);
    }

    /** 拖动结束后把侧栏宽度/输入栏高度写回 settings.yaml, 下次启动沿用(失败只记日志, 不影响当前会话)。 */
    private void SaveLayoutSizes()
    {
        if (_settings is null)
            return;
        try
        {
            if (_rightPanelWidth is { } width)
                _settings.SidebarWidth = width;
            if (_inputHeight is { } rows)
                _settings.InputHeight = rows;
        }
        catch (Exception error)
        {
            _ctx.LoggerFor("tui").Warn($"failed to save layout sizes: {error.Message}");
        }
    }

    /** 单窗格右栏的滚动偏移(供测试断言; 滚轮在右栏上滚动的是它而不是正文)。 */
    internal int RightPanelScrollOffset => InputPane.RightPanelScrollOffset;

    /** 命中分隔线(含左右/上下各 1 格容差, 便于抓取)。 */
    private SplitDivider? FindDividerAt(int cellX, int cellY)
    {
        foreach (var divider in _paneLayout.Dividers)
        {
            var hit = divider.Orientation == SplitOrientation.Vertical
                ? Math.Abs(cellX - divider.X) <= 1 && cellY >= divider.Y && cellY < divider.Y + divider.Length
                : Math.Abs(cellY - divider.Y) <= 1 && cellX >= divider.X && cellX < divider.X + divider.Length;
            if (hit)
                return divider;
        }
        return null;
    }

    /** 拖动分隔线: 指针轴向坐标换算成比例(两侧至少各留 MinimumPaneExtent 格), 立即重算布局。 */
    private void DragDivider(SplitDivider divider, int cellX, int cellY)
    {
        if (!PaneTree.TryGetRatio(_paneTree, divider.Path, out var currentRatio))
            return;
        var along = divider.Orientation == SplitOrientation.Vertical ? cellX : cellY;
        var ratio = LayoutEngine.RatioForDrag(divider, along, currentRatio);
        if (Math.Abs(ratio - currentRatio) < 1e-9)
            return;
        _paneTree = PaneTree.Resize(_paneTree, divider.Path, ratio);
        _paneLayout = LayoutEngine.EvaluatePanes(_paneTree, _paneArea);
    }

    /** 命中 transcript 的窗格与矩形(单窗格=主区, 多窗格=去掉头部行的窗格体)。 */
    private bool TryResolveTranscript(int cellX, int cellY, UiLayout layout, out ChatPane pane, out ConsoleRect rect)
    {
        pane = null!;
        rect = default;
        if (_panes.Count == 1)
        {
            if (FocusedPane is not ChatPane single || !layout.Main.Contains(cellX, cellY))
                return false;
            pane = single;
            rect = layout.Main;
            return true;
        }

        var hit = _paneLayout.HitTest(cellX, cellY);
        if (hit is null || !_panes.TryGetValue(hit.Value, out var view) || view is not ChatPane chatPane)
            return false;
        var placement = _paneLayout.Panes.FirstOrDefault(candidate => candidate.PaneId == hit.Value);
        if (placement.Rect.Width <= 0 || placement.Rect.Height <= 1)
            return false;
        rect = new ConsoleRect(placement.Rect.X, placement.Rect.Y + 1, placement.Rect.Width, placement.Rect.Height - 1);
        pane = chatPane;
        return rect.Contains(cellX, cellY);
    }

    public void Draw(CellGrid grid, UiLayout layout)
    {
        grid.Clear();
        layout = Effective(layout);
        // pty 层视图: 整屏换层 —— 侧栏与输入栏都让位(会话/pane 不消失, 只是不在这一层显示),
        // 一排 pty 与一排 pane 并排画既没有意义, 也会把命令行的宽高挤坏。
        if (_ptyLayer is not null)
        {
            var fullWidth = layout.Main.Width + layout.RightPanel.Width + (layout.RightPanel.Width > 0 ? 1 : 0);
            layout = new UiLayout(
                new ConsoleRect(0, 0, fullWidth, Math.Max(1, layout.Input.Y)),
                new ConsoleRect(0, 0, 0, 0),
                new ConsoleRect(0, 0, 0, 0),
                layout.Status);
        }

        // 窗格区按"正文宽"布局(不含右侧栏): 分屏后右栏依然保留, 窗格只在正文区里再分, 不会把侧栏挤掉。
        // 分隔线由 _paneLayout 派生, 与窗格同源, 不会因基数不一致而错位。
        _paneArea = new ConsoleRect(0, 0, layout.Main.Width, Math.Max(0, layout.Main.Height));
        // 浮层(总览/子代理列表)的正文区域: 限于正文宽, 避免居中基准包含侧栏而压到侧栏上
        _overlayArea = _paneArea;
        _paneLayout = LayoutEngine.EvaluatePanes(_paneTree, _paneArea);
        SyncShellPaneSizes();

        if (_ptyLayer is { } layer)
        {
            DrawPaneHeader(grid, layer, _paneArea, focused: true);
            if (_paneArea.Height > 1)
                layer.DrawTranscript(grid, new ConsoleRect(_paneArea.X, _paneArea.Y + 1, _paneArea.Width, _paneArea.Height - 1));
            // 审批/提问必须可见可答(否则在这一层里会卡住): 有挂起项时把输入栏照常画出来。
            if (InputPane.PendingApproval is not null || InputPane.PendingQuestions is not null)
                InputPane.DrawInput(grid, layout.Input);
            DrawStatusLine(grid, layout.Status);
            if (_overviewActive)
                DrawOverview(grid);
            if (_agentsActive)
                DrawAgentList(grid);
            ChoicePane?.DrawChoicePrompt(grid);
            return;
        }

        if (_panes.Count == 1)
        {
            var only = _panes.Values.First();
            if (only is ChatPane chatPane && layout.RightPanel.Width > 0)
            {
                only.DrawTranscript(grid, layout.Main);
                chatPane.DrawRightPanel(grid, layout.RightPanel);
            }
            else
            {
                only.DrawTranscript(grid, layout.Main);
            }
        }
        else
        {
            DrawPanes(grid);
            // 多窗格也保留右侧栏: 侧栏是窗口级的(InputPane 的上下文/MCP 面板), 不该因为分屏就消失。
            if (layout.RightPanel.Width > 0)
                InputPane.DrawRightPanel(grid, layout.RightPanel);
        }

        InputPane.DrawInput(grid, layout.Input);
        DrawStatusLine(grid, layout.Status);
        DrawDividers(grid, layout);
        if (_overviewActive)
            DrawOverview(grid);
        if (_agentsActive)
            DrawAgentList(grid);
        DrawPaneOverlays(grid, layout);
        ChoicePane?.DrawChoicePrompt(grid);
    }

    /**
     * 命令/mention 浮层永远最后画: 它属于整帧最上层, 否则会被右栏/输入/状态/分隔线/窗口级浮层覆盖。
     * 单窗格用正文矩形(含右侧栏时即 layout.Main); 多窗格逐一用各窗格矩形。
     */
    private void DrawPaneOverlays(CellGrid grid, UiLayout layout)
    {
        if (_panes.Count == 1)
        {
            var only = _panes.Values.First();
            if (only.HasOverlay)
                only.DrawOverlay(grid, layout.Main);
            return;
        }
        foreach (var placement in _paneLayout.Panes)
        {
            if (!_panes.TryGetValue(placement.PaneId, out var pane) || !pane.HasOverlay)
                continue;
            var rect = placement.Rect;
            if (rect.Width > 0 && rect.Height > 1)
                pane.DrawOverlay(grid, new ConsoleRect(rect.X, rect.Y + 1, rect.Width, rect.Height - 1));
        }
    }

    public void RequestExit()
        => _exitRequested = true;

    public void Dispose()
    {
        // The catalog refresh writes models-dev.json into the home: let it observe the cancellation and finish before the home may be removed.
        // CI (Windows): a test deleted the home while the refresh still held models-dev.json.<guid>.tmp open.
        _lifetime.Cancel();
        _providerCatalogRefresh.GetAwaiter().GetResult();
        _paneTools?.Dispose();
        _editorTerminalRegistration?.Dispose();
        foreach (var pane in _panes.Values)
            pane.Dispose();
        _unsubscribe.Invoke();
        _approvalSubscription.Invoke();
        _questionsSubscription.Invoke();
        _skillChangeSubscription.Invoke();
        _subagentStartSubscription.Invoke();
        _subagentEndSubscription.Invoke();
    }

    internal void QueueAction(Action action)
    {
        lock (_gate)
            _pendingActions.Enqueue(action);
        WakeHook?.Invoke();
    }

    /** 外部(如 shell 输出泵)请求重绘: 只推版本号, 真正的合并由主循环完成。 */
    internal void Invalidate()
        => RenderVersion++;

    /** 窗格目录条目: pane_read/pane_list 工具的只读元数据。 */
    internal sealed record PaneCatalogEntry(
        int Id,
        TuiPaneKind Kind,
        string Title,
        string? SessionId,
        string? PtyId,
        string? PtyCommand,
        bool Focused,
        bool Exited);

    /** 把读取编排到 UI 线程执行(窗格集合只在 UI 线程增删); 结果经 TCS 跨线程返回。 */
    internal Task<T> DispatchAsync<T>(Func<T> read)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueAction(() =>
        {
            try
            {
                completion.TrySetResult(read());
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        });
        return completion.Task;
    }

    internal Task<IReadOnlyList<PaneCatalogEntry>> SnapshotPanesAsync()
        => DispatchAsync(() =>
        {
            var entries = new List<PaneCatalogEntry>(_panes.Count);
            foreach (var pane in _panes.Values)
            {
                // pty 层是"一层"而不是会话里的窗格: 不进目录(pane_list/总览都不该把它当 pane)。
                if (pane is ShellPane { LayerPtyId: { Length: > 0 } })
                    continue;
                entries.Add(CatalogEntryOf(pane));
            }
            return (IReadOnlyList<PaneCatalogEntry>)entries;
        });

    /** 读取工具挂载: 交互形态由 TuiRunner 调用一次, 随 ChatWindow.Dispose 一并注销。 */
    internal void RegisterPaneTools()
    {
        _paneTools ??= PaneReadTools.Apply(_ctx, this);
        _editorTerminalRegistration ??= _ctx.Provide("editorTerminalHost", new EditorTerminalHost(this));
    }

    private IDisposable? _paneTools;

    internal Task<PaneCatalogEntry?> FindPaneAsync(int paneId)
        => DispatchAsync(() => _panes.TryGetValue(paneId, out var pane) ? CatalogEntryOf(pane) : null);

    internal Task<IReadOnlyList<string>?> SnapshotPaneLinesAsync(int paneId)
        => DispatchAsync(() => _panes.TryGetValue(paneId, out var pane) ? pane.SnapshotLines() : null);

    /** 常驻桥发布用: 一次派发内取本地窗格目录 + 每个窗格尾行, 作为发往 daemon 的快照。 */
    internal Task<IReadOnlyList<PtyPaneSnapshotDto>> SnapshotPaneSnapshotsAsync(int maxLines)
        => DispatchAsync<IReadOnlyList<PtyPaneSnapshotDto>>(() =>
        [
            .. Catalog().Select(entry => new PtyPaneSnapshotDto
            {
                Id = entry.Id,
                Kind = entry.Kind.ToString().ToLowerInvariant(),
                Title = entry.Title,
                SessionId = entry.SessionId,
                PtyId = entry.PtyId,
                Command = entry.PtyCommand,
                Focused = entry.Focused,
                Exited = entry.Exited,
                Lines = _panes.TryGetValue(entry.Id, out var pane) ? [.. pane.SnapshotLines().TakeLast(maxLines)] : null,
            }),
        ]);

    /** 在 UI 线程把一条窗格输入(kind: focus/text/key)应用到目标窗格; 返回人类可读结果。 */
    internal string ApplyPaneInput(string kind, int paneId, string payload)
    {
        if (!_panes.TryGetValue(paneId, out var pane))
            throw new InvalidOperationException($"pane {paneId} does not exist");
        switch (kind)
        {
            case "focus":
                FocusPane(paneId);
                return $"focused pane {paneId}";
            case "text" when pane is ChatPane chat:
                chat.SendUserText(payload);
                return $"sent text to pane {paneId}";
            case "text" when pane is ShellPane shell:
                shell.HandleText(payload);
                return $"wrote text to pane {paneId}";
            case "key" when pane is ShellPane keys:
                keys.HandleText(payload);
                return $"wrote keys to pane {paneId}";
            default:
                throw new InvalidOperationException($"pane {paneId} does not accept kind \"{kind}\"");
        }
    }

    /** 刷新并返回其他进程发布的远程窗格(pty id + 快照); 直接用返回值, 不等 UI 线程应用 _daemonPtys。 */
    internal async Task<IReadOnlyList<(string PtyId, PtyPaneSnapshotDto Pane)>> RemotePanesAsync()
    {
        var sessions = await RefreshDaemonPtysAsync();
        return [.. sessions.SelectMany(pty => (pty.Panes ?? []).Select(pane => (pty.Id, pane)))];
    }

    /** daemon pty 访问入口; 默认走真实客户端, 测试可替换为假实现。 */
    internal IDaemonPtyAccess DaemonPtyAccess { get; set; } = new RealDaemonPtyAccess();

    /** 本窗格所属 agent 会话 id(常驻桥上报归属用)。 */
    internal string AgentSessionId => InputPane.Agent.Id.Value;

    private PaneCatalogEntry CatalogEntryOf(ITuiPane pane)
        => new(
            pane.Id,
            pane.Kind,
            pane.PaneTitle,
            pane.Session?.Id.Value,
            (pane as ShellPane)?.PtyId,
            (pane as ShellPane)?.PtyCommand,
            pane.Id == _focusedPaneId,
            (pane as ShellPane)?.Exited ?? false);

    /** 读任意会话(含无窗格的后台子代理会话)的 transcript 文本; 未知 id 返回 null。渲染在独立渲染器上进行, 不触碰任何 pane 状态。 */
    internal IReadOnlyList<string>? SnapshotSessionLines(SessionId id)
    {
        var session = _subagents.FindSession(id);
        if (session is null)
            return null;
        var renderer = new TranscriptRenderer(MaintenancePresenter);
        foreach (var sessionEvent in session.SnapshotEvents())
            renderer.AppendSessionEvent(sessionEvent, replay: true);
        return renderer.SnapshotLines();
    }

    internal IReadOnlyList<MentionSessionInfo> CurrentSessions()
    {
        if (_sessionInfos is not null)
            return _sessionInfos;

        var result = new List<MentionSessionInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_persistence is not null)
        {
            try
            {
                foreach (var snapshot in _persistence.List())
                {
                    // 子代理会话不出现在会话选择浮层里(与 GUI 侧栏一致)。
                    if (snapshot.Header.IsSubagent)
                        continue;
                    var info = MentionSessionInfo.FromSnapshot(snapshot);
                    if (seen.Add(info.Id))
                        result.Add(info);
                }
            }
            catch (Exception error)
            {
                _ctx.LoggerFor("tui").Warn($"failed to list persistent sessions: {error.Message}");
            }
        }

        foreach (var agent in _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)?.List() ?? [])
        {
            if (agent.Session.Header.IsSubagent)
                continue;
            var info = new MentionSessionInfo(agent.Id.ToString(), agent.Session.Header.Title, null, agent.Session.Header.CreatedAt);
            if (seen.Add(info.Id))
                result.Add(info);
        }

        _sessionInfos = result;
        return result;
    }

    internal void InvalidateSessionInfos()
        => _sessionInfos = null;

    private const int GitRefreshMinIntervalMs = 1500;

    private IReadOnlyList<string>? _gitLines;
    private string? _gitCwd;
    private long _gitRefreshedAt;
    private int _gitRefreshing;

    /** 右栏 Git 变更: 只读缓存(未就绪显示"计算中~"), 后台刷新, 不阻塞绘制/GPU 帧路径。 */
    internal IReadOnlyList<string> GitLines()
    {
        var cwd = InputPane.Agent.Session.Header.Cwd;
        if (string.IsNullOrWhiteSpace(cwd))
            return ["(无工作目录)"];
        var stale = Environment.TickCount64 - _gitRefreshedAt >= GitRefreshMinIntervalMs;
        if ((_gitLines is null || !string.Equals(_gitCwd, cwd, StringComparison.Ordinal) || stale)
            && Interlocked.CompareExchange(ref _gitRefreshing, 1, 0) == 0)
            _ = RefreshGitLinesAsync(cwd);
        return _gitLines ?? ["计算中~"];
    }

    private async Task RefreshGitLinesAsync(string cwd)
    {
        try
        {
            var lines = await Task.Run(() => LoadGitLinesAsync(_ctx, cwd, _lifetime.Token));
            QueueAction(() =>
            {
                _gitLines = lines;
                _gitCwd = cwd;
                _gitRefreshedAt = Environment.TickCount64;
                Invalidate();
            });
        }
        catch (Exception error)
        {
            _ctx.LoggerFor("tui").Warn($"git panel failed: {error.Message}");
            QueueAction(() =>
            {
                _gitLines = ["git 变更读取失败"];
                _gitRefreshedAt = Environment.TickCount64;
                Invalidate();
            });
        }
        finally
        {
            Interlocked.Exchange(ref _gitRefreshing, 0);
        }
    }

    internal static Task<IReadOnlyList<string>> LoadGitLinesAsync(Context ctx, string cwd, CancellationToken cancellationToken = default)
    {
        var git = ctx.Get<IGitService>(IGitService.ServiceName, false);
        return git is null
            ? Task.FromResult<IReadOnlyList<string>>(["(非 git 仓库或 git 不可用)"])
            : LoadGitLinesAsync(git, cwd, cancellationToken);
    }

    internal static async Task<IReadOnlyList<string>> LoadGitLinesAsync(IGitService git, string cwd, CancellationToken cancellationToken = default)
    {
        GitStatus status;
        try
        {
            status = await git.StatusAsync(cwd, cancellationToken);
        }
        catch (Exception)
        {
            return ["(非 git 仓库或 git 不可用)"];
        }
        if (status.IsClean && status.Branch.Oid is null)
            return ["(无变更)"];
        var target = status.IsClean ? GitDiffTarget.OfCommit(status.Branch.Oid!) : GitDiffTarget.Uncommitted;
        var summary = await git.DiffSummaryAsync(cwd, target, cancellationToken: cancellationToken);
        return status.IsClean && summary.Files.Count == 0
            ? ["(无变更)"]
            : GitPanel.Format(summary, status.IsClean ? "上次提交" : "未提交改动");
    }

    internal IReadOnlyList<string> McpLines()
    {
        if (_mcpPanelLines is not null)
            return _mcpPanelLines;
        try
        {
            var servers = HarnessSettings.Load(_home).McpServers;
            _mcpPanelLines = servers.Count == 0
                ? ["无 MCP 服务器"]
                : servers.Select(entry =>
                    {
                        var target = entry.Value.Url
                            ?? (entry.Value.Command is { Count: > 0 } command ? string.Join(' ', command) : "-");
                        var suffix = entry.Value.Enabled ? "" : " (disabled)";
                        return $"{entry.Key}: {target}{suffix}";
                    })
                    .ToList();
        }
        catch (Exception error)
        {
            _mcpPanelLines = [$"MCP 配置读取失败: {error.Message}"];
        }
        return _mcpPanelLines;
    }

    internal void RunSlashCommand(ChatPane pane, string text)
        => RunSlashCommandAsync(pane, text);

    private async void RunSlashCommandAsync(ChatPane pane, string text)
    {
        var name = text.Split(' ', 2)[0];
        var verb = name.Length > 1 && name[0] == '/' ? name[1..] : name;
        if (string.Equals(verb, "quit", StringComparison.OrdinalIgnoreCase))
        {
            // /quit 可抛弃, 但仍认它: 不再单独给菜单项。
            RequestExit();
            return;
        }
        var local = LocalCommands.FirstOrDefault(command => string.Equals(verb, command.Name, StringComparison.OrdinalIgnoreCase));
        if (local is null)
        {
            await RunRegisteredCommandAsync(pane, text);
            return;
        }
        // async void 链上的异常会直接终止进程: 本地命令的失败只报告给用户。
        try
        {
            await local.Run(pane, text);
        }
        catch (Exception error)
        {
            var message = error.Message;
            QueueAction(() => pane.AppendRaw($"  command failed: {message}\n"));
        }
    }

    private async Task RunRegisteredCommandAsync(ChatPane pane, string text)
    {
        var commands = _ctx.Get<CommandsService>(CommandsService.ServiceName);
        if (commands is null)
        {
            // 无法判断是不是命令: 按普通消息发出(与"写错了"同一处理)。
            pane.SendUserText(text);
            return;
        }

        try
        {
            var execution = await commands.Execute(pane.Agent, text);
            if (execution is null)
            {
                // 首 token 不是已知命令: 用户为自己的输入负责, 整行当普通消息发给模型。
                pane.SendUserText(text);
                return;
            }

            string? resultText = execution.Result switch
            {
                CommandResult.Success { Text: { } successText } when successText.Length > 0 => $"  {successText}\n",
                CommandResult.Error error => $"  {error.Text}\n",
                _ => null,
            };
            if (resultText is not null)
            {
                var captured = resultText;
                QueueAction(() => pane.AppendRaw(captured));
            }

            // 命令 + 提示词: 提示词由命令决定(成功或失败都可能带), 作为下一步用户消息发出。
            var followup = execution.Result switch
            {
                CommandResult.Success success => success.FollowupPrompt,
                CommandResult.Error error => error.FollowupPrompt,
                _ => null,
            };
            if (followup is { Length: > 0 } prompt)
                pane.SendUserText(prompt);
        }
        catch (Exception error)
        {
            var message = error.Message;
            QueueAction(() => pane.AppendRaw($"  command failed: {message}\n"));
        }
    }

    private IReadOnlyList<LocalCommand>? _localCommands;

    /** TUI 本地命令(未注册到 CommandsService): 浮层菜单项与分发表同源, 避免新增命令只改一处。 */
    private IReadOnlyList<LocalCommand> LocalCommands => _localCommands ??=
    [
        new("new", "开始新会话(可带目录)", NewSession),
        new("resume", "恢复历史会话", ResumeSession),
        new("session", "列出/恢复/删除会话", ListSessions),
        new("detach", "脱离会话交给 daemon", DetachSession),
        new("gpu", "选择 GPU / 渲染后端", (pane, text) => { SelectGpu(pane, text); return Task.CompletedTask; },
            [new CommandArgumentSchema("adapter", "select", "GPU 适配器(选择并回车即写入, 重启生效)")]),
        new("exit", "退出 TUI", (_, _) => { RequestExit(); return Task.CompletedTask; }),
        new("keys", "列出当前按键绑定", (pane, _) =>
        {
            // 逐行追加: 整段长文本会被折叠成一行预览, 快捷键表要直接可读。
            pane.AppendRaw($"  快捷键(settings.yaml: plugins.\"{TuiSettings.Package}\".keys 可改):\n");
            foreach (var line in _keys.Describe())
                pane.AppendRaw($"    {line}\n");
            return Task.CompletedTask;
        }),
        new("timestamp", "定位/回退/派生到某条用户消息", TimestampCommand,
            [new CommandArgumentSchema("action", "select", "jump(查看) / revert(截断到该消息之前) / fork(派生新会话)"),
             new CommandArgumentSchema("seq", "select", "用户消息 seq(见 /timestamp 无参数列出)")]),
    ];

    /** 供 ChatPane 追加进命令浮层(按名字去重)。 */
    internal IReadOnlyList<CommandDescriptor> LocalCommandDescriptors
        => [.. LocalCommands.Select(command => new CommandDescriptor(
            command.Name,
            command.Description,
            ArgumentSchemas: command.ArgumentSchemas))];

    private sealed record LocalCommand(
        string Name,
        string Description,
        Func<ChatPane, string, Task> Run,
        IReadOnlyList<CommandArgumentSchema>? ArgumentSchemas = null);

    /**
     * `/session` 浮层候选: 显示"会话标题 + 带时区的时间戳", **写入的是会话 id**
     * (标题可能为空或重复, 拿它当值会让命令选错会话); 空标题退回用 id 显示。
     */
    internal IReadOnlyList<CommandCandidate> SessionCandidates()
        => [.. CurrentSessions().Select(session => new CommandCandidate(
            session.Id,
            string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title,
            SessionTimestamp(session.CreatedAt)))];

    /** 会话创建时间戳(本地时区 + 偏移, 如 `2026-10-13 15:54 +08:00`); 未知(旧日志没有该字段)时返回 null 不显示。 */
    internal static string? SessionTimestamp(long createdAtEpochMilliseconds)
        => createdAtEpochMilliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(createdAtEpochMilliseconds).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)
            : null;

    /**
     * `/timestamp seq` 的候选: 本会话用户消息 + 工作区命中的 IDE 历史(§6 统一时间线)。
     * 消息项写入 `#<seq>`; IDE 项写入 `ide|<provider>|<epoch-ms>|<path>`(路径放最后, 自身可含空格)。
     */
    internal IReadOnlyList<CommandCandidate> TimestampCandidates()
    {
        var candidates = new List<(long Timestamp, CommandCandidate Candidate)>();
        _ideTimelineMoments.Clear();
        _ideTimelineSession = null;
        foreach (var (seq, time, text) in UserMessages(InputPane.Agent.Session))
        {
            var token = $"#{seq.ToString(CultureInfo.InvariantCulture)}";
            candidates.Add((time, new CommandCandidate(token, $"{token} {Preview(text)}", SessionTimestamp(time))));
        }

        var service = _ctx.Get<IIdeHistoryService>(IIdeHistoryService.ServiceName, false);
        // 默认不并进时间线(插件参数 timeline=false): IDE 自动保存很密, 会淹掉用户消息。
        if (service is null || !service.TimelineEnabled)
            return [.. candidates.Select(item => item.Candidate)];
        var workspace = InputPane.CurrentCwd();
        _ideTimelineSession = InputPane.Agent.Session.Id;
        if (workspace is not null && service is IIdeHistoryTimeline timeline)
        {
            foreach (var moment in timeline.QueryTimeline(workspace))
            {
                var token = $"ide-group|{moment.Timestamp.ToString(CultureInfo.InvariantCulture)}";
                _ideTimelineMoments[token] = moment;
                var first = moment.Entries[0];
                candidates.Add((moment.Timestamp, new CommandCandidate(token,
                    $"{IdeEntryLabel(workspace, first)}{(moment.Entries.Count > 1 ? $" +{moment.Entries.Count - 1} revisions" : "")}",
                    $"history window; {moment.Entries.Count(entry => entry.MatchesGitHead is true)} match git HEAD")));
            }
        }
        else
            foreach (var entry in service.Query(workspace, from: null, to: null))
                candidates.Add((entry.Timestamp, new CommandCandidate(IdeTargetValue(entry), IdeEntryLabel(workspace, entry), entry.Kind)));
        return [.. candidates.OrderByDescending(item => item.Timestamp).Select(item => item.Candidate)];
    }

    /** IDE 历史项在选单里的值编码(可被 `/timestamp` 解析回条目)。 */
    internal static string IdeTargetValue(IdeHistoryEntry entry)
        => $"ide|{entry.Provider}|{entry.Timestamp.ToString(CultureInfo.InvariantCulture)}|{entry.Path}";

    internal static (string Provider, long Timestamp, string Path)? ParseIdeTarget(string value)
    {
        var parts = value.Split('|', 4);
        return parts.Length == 4 && parts[0] == "ide"
            && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp)
            ? (parts[1], timestamp, parts[3])
            : null;
    }

    /** IDE 项的显示: 工作区内用相对路径, 右侧带本地时间与来源。 */
    private static string IdeEntryLabel(string? workspaceRoot, IdeHistoryEntry entry)
    {
        var path = entry.Path;
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var relative = Path.GetRelativePath(workspaceRoot, entry.Path);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                path = relative;
        }
        return $"{path} @ {entry.Time.ToLocalTime():HH:mm:ss} ({entry.Provider}){(entry.MatchesGitHead is true ? " [git HEAD]" : "")}";
    }

    /** `/timestamp [jump|revert|fork] <#seq|ide|...>`: 无参数列出时间线; revert 回退文件+会话, fork 派生新会话。 */
    private async Task TimestampCommand(ChatPane pane, string text)
    {
        var messages = UserMessages(pane.Agent.Session);
        var space = text.IndexOf(' ');
        var arguments = space < 0 ? "" : text[(space + 1)..].Trim();
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            foreach (var candidate in TimestampCandidates())
                pane.AppendRaw($"  {candidate.Value} {candidate.Label}\n");
            return;
        }

        var action = parts[0].ToLowerInvariant();
        if (parts.Length < 2)
        {
            pane.AppendRaw("  usage: /timestamp <jump|revert|fork> <#seq|ide 项>\n");
            return;
        }

        var target = parts[1];
        var confirm = target.StartsWith("--confirm ", StringComparison.Ordinal);
        if (confirm)
            target = target["--confirm ".Length..];
        if (target.StartsWith("ide-group|", StringComparison.Ordinal))
        {
            if (_ideTimelineSession == pane.Agent.Session.Id && _ideTimelineMoments.TryGetValue(target, out var moment))
                await IdeTimestampMomentActionAsync(pane, action, target, moment, messages, confirm);
            else
                pane.AppendRaw("  History window expired; open /timestamp again.\n");
            return;
        }
        if (ParseIdeTarget(target) is { } ide)
        {
            var service = _ctx.Get<IIdeHistoryService>(IIdeHistoryService.ServiceName, false);
            var entry = service?.Query(pane.CurrentCwd(), ide.Timestamp, ide.Timestamp, ide.Path)
                .FirstOrDefault(candidate => candidate.Provider == ide.Provider && candidate.Timestamp == ide.Timestamp);
            if (entry is null)
                pane.AppendRaw("  History revision is unavailable.\n");
            else
                await IdeTimestampMomentActionAsync(pane, action, target, new IdeHistoryMoment(ide.Timestamp, [entry]), messages, confirm);
            return;
        }

        var seqToken = target.TrimStart('#');
        if (!long.TryParse(seqToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetSeq))
        {
            pane.AppendRaw("  usage: /timestamp <jump|revert|fork> <#seq|ide 项>\n");
            return;
        }

        var message = messages.FirstOrDefault(entry => entry.Seq == targetSeq);
        switch (action)
        {
            case "jump":
                await JumpToMessage(pane, message, targetSeq);
                return;
            case "revert":
                await RevertToAsync(pane, targetSeq, message.Text);
                return;
            case "fork":
                await ForkAtAsync(pane, targetSeq);
                return;
            default:
                pane.AppendRaw("  usage: /timestamp <jump|revert|fork> <#seq|ide 项>\n");
                return;
        }
    }

    /** A grouped revision previews every affected file and keeps the selected IDE content authoritative over checkpoints. */
    private async Task IdeTimestampMomentActionAsync(
        ChatPane pane,
        string action,
        string target,
        IdeHistoryMoment moment,
        IReadOnlyList<(long Seq, long Time, string Text)> messages,
        bool confirm)
    {
        if (action == "jump")
        {
            foreach (var entry in moment.Entries)
                pane.AppendRaw($"  {IdeEntryLabel(pane.CurrentCwd(), entry)}\n");
            return;
        }
        var nearest = NearestMessageSeq(messages, moment.Timestamp);
        if (action == "fork")
        {
            if (nearest is { } seq)
                await ForkAtAsync(pane, seq);
            else
                pane.AppendRaw("  No user message exists before this history window.\n");
            return;
        }
        if (action != "revert")
        {
            pane.AppendRaw("  usage: /timestamp <jump|revert|fork> <#seq|ide revision>\n");
            return;
        }
        if (_ctx.Get<IIdeHistoryRestore>(IIdeHistoryService.ServiceName, false) is not { } restore
            || pane.CurrentCwd() is not { } workspace)
        {
            pane.AppendRaw("  Safe history restore is unavailable.\n");
            return;
        }
        var session = pane.Agent.Session.Id;
        if (!confirm)
        {
            try
            {
                var previews = moment.Entries
                    .GroupBy(entry => entry.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    .Select(group => restore.PreviewRestore(workspace, group.OrderByDescending(entry => entry.Timestamp).First()))
                    .ToArray();
                _ideRestorePreview = (session, target, previews);
                foreach (var preview in previews)
                {
                    pane.AppendRaw($"  {IdeEntryLabel(workspace, preview.Entry)}\n{preview.Diff}\n");
                    if (preview.ModifiedSinceEntry)
                        pane.AppendRaw("  This file changed after the revision; confirmation will overwrite those changes.\n");
                }
                pane.AppendRaw($"  Preview only; no files changed. Confirm: /timestamp revert --confirm {target}\n");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _ideRestorePreview = null;
                pane.AppendRaw("  History preview failed; no files changed.\n");
            }
            return;
        }
        if (_ideRestorePreview is not { } pending || pending.Session != session || pending.Target != target)
        {
            pane.AppendRaw("  Request a preview for this history revision before confirming.\n");
            return;
        }
        _ideRestorePreview = null;
        foreach (var preview in pending.Previews)
        {
            var result = restore.Restore(preview, confirmModified: true);
            if (!result.Restored)
            {
                pane.AppendRaw($"  Restore stopped: {result.Failure}; session unchanged. Backups for completed files remain in DSH storage.\n");
                return;
            }
            pane.AppendRaw($"  Restored {IdeEntryLabel(workspace, preview.Entry)}; backup: {result.BackupPath}\n");
        }
        if (nearest is { } messageSeq)
            await RevertToAsync(pane, messageSeq, messages.First(entry => entry.Seq == messageSeq).Text, restoreFiles: false);
        else
            pane.AppendRaw("  Files restored; no earlier user message exists to truncate.\n");
    }

    /** 时间 ≤ 目标时刻的最近一条用户消息(消息按时间递增, 取最后一个满足的就是它)。 */
    private static long? NearestMessageSeq(IReadOnlyList<(long Seq, long Time, string Text)> messages, long timestamp)
    {
        long? nearest = null;
        foreach (var message in messages)
        {
            if (message.Time <= timestamp)
                nearest = message.Seq;
        }
        return nearest;
    }


    /** 本会话里用户自己发的消息(seq / 事件时间 / 文本), 供时间线与"IDE 时间点 → 最近消息"映射。 */
    private static List<(long Seq, long Time, string Text)> UserMessages(Session session)
    {
        var messages = new List<(long Seq, long Time, string Text)>();
        foreach (var sessionEvent in session.SnapshotEvents())
        {
            if (sessionEvent.Data is not UserMessagePayload user || user.Message.Source is not UserMessageSource)
                continue;
            var text = string.Concat(user.Message.Content.OfType<TextBlock>().Select(block => block.Text)).Trim();
            if (text.Length > 0)
                messages.Add((sessionEvent.Seq, sessionEvent.Time, text));
        }
        return messages;
    }

    /** jump: 只滚到该消息(不改会话/文件); 用消息首行文本在视口缓存里定位。 */
    private static Task JumpToMessage(ChatPane pane, (long Seq, long Time, string Text) target, long seq)
    {
        var firstLine = FirstLine(target.Text);
        if (firstLine.Length == 0)
        {
            pane.AppendRaw($"  jump: 消息 #{seq} 无文本\n");
            return Task.CompletedTask;
        }

        // 只定位: 目标消息滚到顶上; 不打印 "jumped to"(那会把视图重新贴回底部, 跳转看不到效果)。
        if (!pane.TryScrollToMessage(firstLine))
            pane.AppendRaw($"  jump: 未在视口缓存中找到 #{seq}(先让它渲染一次再试)\n");
        return Task.CompletedTask;
    }

    /** 会话截断到该消息之前并把消息回填输入区; 文件侧先按检查点恢复到 <= seq。 */
    private async Task RevertToAsync(ChatPane pane, long seq, string targetText, bool restoreFiles = true)
    {
        var restore = _ctx.Get<ICheckpointRestore>(ICheckpointRestore.ServiceName, false);
        if (restoreFiles && restore is not null && pane.CurrentCwd() is { Length: > 0 } cwd)
        {
            try
            {
                if (await restore.RestoreToSeqAsync(cwd, seq, CancellationToken.None) is { Length: > 0 } commit)
                    pane.AppendRaw($"  文件已恢复到检查点 {commit[..Math.Min(8, commit.Length)]}\n");
            }
            catch (Exception error)
            {
                pane.AppendRaw($"  文件恢复失败(会话未截断): {error.Message}\n");
                return;
            }
        }

        if (!pane.Agent.Session.TryTruncateTo(seq))
        {
            pane.AppendRaw($"  revert 失败: seq {seq} 超出可截断范围\n");
            return;
        }

        pane.Input = targetText;
        pane.Cursor = pane.Input.Length;
        pane.StickToBottom = true;
        pane.AppendRaw($"  reverted: 会话已截断到 #{seq} 之前, 消息已回填输入区\n");
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed.Length <= 16 ? trimmed : trimmed[..16];
        }
        return "";
    }

    /**
     * 派生新会话(不动当前会话), 带上 [0, seq] —— **含被选中的那条消息**。
     * 不带它的话"从第一条用户消息 fork"会得到一个只有握手的空会话(用户看到的就是空转录)。
     */
    private Task ForkAtAsync(ChatPane pane, long seq)
    {
        var session = pane.Agent.Session;
        var persistence = _ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false);
        if (persistence is null)
        {
            pane.AppendRaw("  fork 不可用: 会话持久化未启用\n");
            return Task.CompletedTask;
        }

        SessionId forked;
        try
        {
            forked = SessionFork.Fork(persistence, session, seq + 1, $"fork of {session.Id} @{seq}", pane.CurrentCwd());
        }
        catch (InvalidOperationException error)
        {
            pane.AppendRaw($"  {error.Message}\n");
            return Task.CompletedTask;
        }

        pane.AppendRaw($"  forked -> {forked} (用 /resume {forked} 打开)\n");
        return Task.CompletedTask;
    }

    private static string Preview(string text)
    {
        var single = text.Replace('\n', ' ');
        return single.Length <= 60 ? single : $"{single[..60]}~";
    }

    private async Task NewSession(ChatPane pane, string text)
    {
        var cwd = text["/new".Length..].Trim();
        if (cwd.Length == 0)
            cwd = Environment.CurrentDirectory;
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var options = pane.Agent.Options;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions(options.Provider, options.Model, options.ReasoningEffort, options.MaxTokens)));
        var newAgent = (AgentLoopAgent)handle.Agent;
        await newAgent.WhenIdle();
        QueueAction(() =>
        {
            if (!IsPaneAlive(pane))
                return;
            pane.SwitchAgent(newAgent);
            pane.AppendRaw($"  new session: {newAgent.Id}\n");
        });
    }

    private async Task ResumeSession(ChatPane pane, string text)
    {
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var raw = text["/resume".Length..].Trim();
        if (raw.Length == 0)
        {
            var sessions = agents.List();
            if (sessions.Count == 0)
            {
                pane.AppendRaw("  no live sessions\n");
                return;
            }

            pane.AppendRaw(string.Join('\n', sessions.Select(agent =>
            {
                var (provider, model) = CurrentModel(agent);
                return $"  {agent.Id}: {provider}/{model}";
            })) + "\n");
            return;
        }

        var sessionId = SessionId.Create(raw);
        if (agents.Get(sessionId) is not AgentLoopAgent target)
        {
            AgentHandle handle;
            var options = pane.Agent.Options;
            try
            {
                handle = await agents.Resume(new ResumeAgentOptions(
                    sessionId,
                    new AgentOptions(options.Provider, options.Model, options.ReasoningEffort, options.MaxTokens)));
            }
            catch (Exception error)
            {
                pane.AppendRaw($"  session cannot be loaded: {raw} - {error.Message}\n");
                return;
            }

            target = (AgentLoopAgent)handle.Agent;
            await target.WhenIdle();
        }

        QueueAction(() =>
        {
            if (!IsPaneAlive(pane))
                return;
            pane.SwitchAgent(target);
            pane.AppendRaw($"  resumed: {target.Id}\n");
        });
    }

    private async Task ListSessions(ChatPane pane, string text)
    {
        var raw = text["/session".Length..].Trim();
        if (raw.StartsWith("delete", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteSessionDelete(pane, raw);
            return;
        }

        var sessions = CurrentSessions();
        if (raw.Length == 0)
        {
            if (sessions.Count == 0)
            {
                pane.AppendRaw("  no sessions\n");
                return;
            }

            pane.AppendRaw(string.Join('\n', sessions.Select(session =>
            {
                var label = string.IsNullOrWhiteSpace(session.Title) ? session.Id : $"{session.Id}: {session.Title}";
                return $"  {label}";
            })) + "\n");
            return;
        }

        var match = sessions.FirstOrDefault(session =>
            string.Equals(session.Id, raw, StringComparison.OrdinalIgnoreCase)
            || string.Equals(session.Title, raw, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            pane.AppendRaw($"  session not found: {raw}\n");
            return;
        }

        await ResumeSession(pane, $"/resume {match.Id}");
    }

    private async Task ExecuteSessionDelete(ChatPane pane, string raw)
    {
        var commands = _ctx.Get<CommandsService>(CommandsService.ServiceName);
        if (commands is null)
        {
            pane.AppendRaw("  commands service unavailable\n");
            return;
        }

        try
        {
            var execution = await commands.Execute(pane.Agent, $"/session {raw}");
            string? resultText = null;
            if (execution is null)
                resultText = $"  unknown command: /session {raw}\n";
            else if (execution.Result is CommandResult.Success { Text: { } successText } && successText.Length > 0)
                resultText = $"  {successText}\n";
            else if (execution.Result is CommandResult.Error error)
                resultText = $"  {error.Text}\n";
            if (resultText is not null)
                pane.AppendRaw(resultText);
            if (execution?.Result is CommandResult.Success)
            {
                pane.CommandMenu?.Close();
                pane.DeleteConfirmSessionId = null;
                InvalidateSessionInfos();
            }
        }
        catch (Exception error)
        {
            pane.AppendRaw($"  command failed: {error.Message}\n");
        }
    }

    /** /gpu [编号|auto]: 列出本机显卡并选择下次启动用的卡; 选择写设置文件(GUI 设置页共用同一键), 重启进程后生效。 */
    private void SelectGpu(ChatPane pane, string text)
    {
        var argument = text.Length > "/gpu".Length ? text["/gpu".Length..].Trim() : "";
        var adapters = GpuCatalog.ListAdapters();
        var labels = GpuCatalog.SelectionLabels();
        if (argument.Length == 0)
        {
            var current = GpuCatalog.LoadSelectedAdapter(_home);
            pane.AppendRaw($"  gpu: current = {(current == GpuCatalog.AutoAdapter ? "auto (system default)" : current)}\n");
            for (var index = 0; index < labels.Count; index++)
                pane.AppendRaw($"    {index}. {labels[index]}\n");
            pane.AppendRaw("  usage: /gpu <number|name> - takes effect after restart\n");
            pane.AppendRaw("  note: 若当前以 CPU 模式运行, 该选择仅下次以 GPU 模式启动时才生效(Linux 经 PRIME 选择器)\n");
            return;
        }

        string selected;
        if (argument.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || argument == "0"
            || argument.Equals(labels.Count > 0 ? labels[0] : GpuCatalog.AutoAdapter, StringComparison.OrdinalIgnoreCase))
            selected = GpuCatalog.AutoAdapter;
        else if (int.TryParse(argument, out var index) && index >= 1 && index <= adapters.Count)
            selected = GpuCatalog.SelectionIdOf(adapters[index - 1]);
        else
        {
            var matched = GpuCatalog.MatchAdapterIndex(labels, argument);
            if (matched <= 0)
            {
                pane.AppendRaw($"  gpu: invalid selection '{argument}' (run /gpu to list)\n");
                return;
            }
            selected = GpuCatalog.SelectionIdOf(adapters[matched - 1]);
        }
        GpuCatalog.SaveSelectedAdapter(_home, selected);
        pane.AppendRaw($"  gpu: selected {(selected == GpuCatalog.AutoAdapter ? "auto (system default)" : selected)} - takes effect after restart\n");
        var windowsNote = GpuCatalog.ApplyWindowsPreference(selected);
        if (windowsNote.Length > 0)
            pane.AppendRaw($"  {windowsNote}\n");
    }

    /** Ctrl+X D: 常驻会话(跑在 daemon 的 pty 上)只放 proxy 走, 自己继续跑; 非常驻(进程内 TUI)沿用 /detach 交接。 */
    private void RequestDetach(ChatPane pane)
    {
        if (IsResidentChild())
        {
            DetachRequested = true;
            // detach 完全静默: 不写 transcript(否则 attach 回来还留着"detached:"字样), 仅记录调试日志。
            _ctx.LoggerFor("tui").Info("detached: 会话继续在 daemon 里运行(`dsharp tui attach` 可接回)");
            return;
        }

        RunSlashCommand(pane, "/detach");
    }

    private static bool IsResidentChild()
        => string.Equals(
            Environment.GetEnvironmentVariable(PtySessionProtocol.ChildVariable),
            "1",
            StringComparison.Ordinal);

    internal bool DetachRequested { get; private set; }

    internal void ClearDetachRequest() => DetachRequested = false;

    /** 交接草稿: detach 走的是新起的 TUI 进程, 未发送的输入只存在旧进程内存里, 由环境变量带过来恢复。 */
    internal void SeedDraft(string text)
    {
        var input = InputPane;
        input.Input = text;
        input.Cursor = text.Length;
        input.RefreshMenus();
    }

    private async Task DetachSession(ChatPane pane, string text)
    {
        var commandLine = text["/detach".Length..].Trim();
        // 常驻会话: 不带参数的 /detach 与 Ctrl+X D 同义 -- 只放 proxy 走, 自己继续跑。
        if (commandLine.Length == 0 && IsResidentChild())
        {
            RequestDetach(pane);
            return;
        }

        var (fileName, arguments) = DetachTarget(commandLine, pane.Agent.Id.ToString());
        // 不带命令 = 起的还是 TUI 本体, 它要鼠标(拖动分隔线/滚轮); 带命令 = 任意命令/普通 shell, 不能打开
        // 宿主终端上报(Unix 上代理原样透传, 报文会被当成命令敲进那个程序)。
        var wantsMouse = commandLine.Length == 0;

        try
        {
            await _ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(pane.Agent.Session);
            await PtyDaemonClient.EnsureRunningAsync();
            var session = await PtyDaemonClient.StartAsync(new PtyDaemonStartParams
            {
                FileName = fileName,
                Arguments = [.. arguments],
                WorkingDirectory = pane.CurrentCwd(),
                Rows = Math.Max(4, _paneArea.Height),
                Columns = Math.Max(20, _paneArea.Width),
                Home = _home.Root,
                Environment = new Dictionary<string, string?>
                {
                    ["DSH_DETACHED_SESSION_ID"] = pane.Agent.Id.ToString(),
                    [PtySessionProtocol.ChildVariable] = "1",
                    [TuiRunner.DetachDraftVariable] = string.IsNullOrEmpty(pane.Input) ? null : pane.Input,
                },
                WantsMouse = wantsMouse,
            });
            // detach 完全静默: 不写 transcript, 仅记录调试日志。
            _ctx.LoggerFor("tui").Info($"detached: {session.Id} - {session.Command} (daemon PTY)");
            RequestExit();
        }
        catch (Exception error)
        {
            pane.AppendRaw($"  detach failed: {error.Message}\n");
        }
    }

    /**
     * `/detach` 的启动目标。
     * 缺省: 把**当前这个 TUI 会话**交给 daemon 里的一个新 TUI 继续跑(等价 tmux 的 detach/attach 语义), attach 回来看到的是完整 TUI 画面而不是一个空 shell。
     * 显式 `/detach <cmd> [args...]` 按命令起一个普通会话。
     */
    internal static (string FileName, string[] Arguments) DetachTarget(string commandLine, string sessionId)
    {
        var parts = commandLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
            return (parts[0], parts.Length > 1 ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : []);
        var executable = Environment.ProcessPath;
        var isDotnetHost = executable is not null
            && string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
        return (executable ?? PtyShell.Resolve(), isDotnetHost
            ? [Environment.GetCommandLineArgs()[0], "tui", "--session", sessionId]
            : ["tui", "--session", sessionId]);
    }

    internal void AddPane(AgentLoopAgent agent, SplitOrientation? orientation)
    {
        var id = _nextPaneId++;
        AddPane(new ChatPane(this, id, agent), orientation);
    }

    /** 通用加入窗格: 分割树按 pane.Id 落位; shell 窗格不参与输入行绑定。 */
    internal void AddPane(ITuiPane pane, SplitOrientation? orientation)
    {
        SplitPaneAt(orientation, pane.Id);
        _panes[pane.Id] = pane;
        _focusedPaneId = pane.Id;
        if (pane is ChatPane)
            _inputPaneId = pane.Id;
        _paneLayout = LayoutEngine.EvaluatePanes(_paneTree, _paneArea);
        RenderVersion++;
    }

    /** 打开一个真 shell 窗格(Dsh.Pty): 终端内形态的临时 shell 与独立窗口形态的终端本体共用。 */
    internal async void AddShellPane()
    {
        var shell = PtyShell.Resolve();
        if (!PtyShell.Exists(shell))
        {
            InputPane.AppendRaw($"  shell unavailable: {shell}\n");
            return;
        }

        var id = _nextPaneId++;
        var columns = Math.Max(20, _paneArea.Width);
        var rows = Math.Max(4, _paneArea.Height - 1);
        try
        {
            var session = await PtyHost.Default.StartAsync(new PtyStartInfo
            {
                FileName = shell,
                Arguments = PtyShell.Arguments(shell),
                WorkingDirectory = InputPane.CurrentCwd(),
                Environment = PtyShell.ChildEnvironment(),
                Rows = rows,
                Columns = columns,
            });
            QueueAction(() => AddPane(new ShellPane(this, id, new LocalTerminalBacking(session), columns, rows), null));
        }
        catch (Exception error)
        {
            QueueAction(() => InputPane.AppendRaw($"  shell failed: {error.Message}\n"));
        }
    }

    internal bool CloseFocusedPane()
    {
        if (_panes.Count <= 1)
            return false;
        if (_panes[_focusedPaneId] is ChatPane && _panes.Values.OfType<ChatPane>().Count() <= 1)
            return false;
        RemovePane(_focusedPaneId);
        return true;
    }

    internal void CloseSubagentView(SubagentPane pane)
        => CloseSubagentView(pane, null);

    /** 关闭只读子代理窗格; 指定 focusSessionId 时把焦点交还展示该会话的窗格。 */
    internal void CloseSubagentView(SubagentPane pane, SessionId? focusSessionId)
    {
        if (!_panes.ContainsKey(pane.Id))
            return;
        RemovePane(pane.Id);
        if (focusSessionId is not null && FindPaneBySessionId(focusSessionId.Value.Value) is { } target)
            FocusPane(target.Id);
    }

    /** 打开只读子代理查看窗格; 同一会话已有窗格时直接聚焦。 */
    internal void OpenSubagentView(SessionId sessionId)
    {
        var existing = FindPaneBySessionId(sessionId.Value);
        if (existing is not null)
        {
            _agentsActive = false;
            FocusPane(existing.Id);
            return;
        }

        var session = _subagents.FindSession(sessionId);
        if (session is null)
        {
            InputPane.AppendRaw($"  subagent session not found: {sessionId}\n");
            return;
        }

        _agentsActive = false;
        var id = _nextPaneId++;
        SplitPaneAt(null, id);
        _panes[id] = new SubagentPane(this, id, session, _subagents);
        _focusedPaneId = id;
        _paneLayout = LayoutEngine.EvaluatePanes(_paneTree, _paneArea);
        RenderVersion++;
    }

    private void SplitPaneAt(SplitOrientation? orientation, int newPaneId)
    {
        var focusedRect = _paneLayout.Panes.FirstOrDefault(placement => placement.PaneId == _focusedPaneId).Rect;
        if (focusedRect.Width <= 0 && focusedRect.Height <= 0)
            focusedRect = new ConsoleRect(0, 0, 80, 24);
        var direction = orientation ?? PaneTree.ChooseOrientation(focusedRect);
        _paneTree = PaneTree.Split(_paneTree, _focusedPaneId, newPaneId, direction);
    }

    private void RemovePane(int paneId)
    {
        _panes.Remove(paneId, out var pane);
        pane?.Dispose();
        if (ReferenceEquals(pane, _ptyLayer))
            _ptyLayer = null;
        var tree = PaneTree.Remove(_paneTree, paneId);
        if (tree is null)
            return;
        _paneTree = tree;
        _focusedPaneId = PaneTree.PaneIds(_paneTree)[0];
        if (!_panes.ContainsKey(_inputPaneId))
        {
            var fallback = _panes.Keys.FirstOrDefault(id => _panes[id] is ChatPane, -1);
            if (fallback >= 0)
                _inputPaneId = fallback;
        }
        _paneLayout = LayoutEngine.EvaluatePanes(_paneTree, _paneArea);
        RenderVersion++;
    }

    internal void FocusPane(int paneId)
    {
        if (paneId == _focusedPaneId || !_panes.ContainsKey(paneId))
            return;
        _focusedPaneId = paneId;
        if (_panes[paneId] is ChatPane)
            _inputPaneId = paneId;
        if (_panes[paneId] is ShellPane shell)
        {
            if (shell.LayerPtyId is { Length: > 0 } layer)
                shell.LayerBanner = PtyLayerStatusText(layer);
            else
                shell.StatusText = $"shell 窗格: 按键直达 shell - {_keys.Keys(TuiAction.ClosePane)} 关闭 - {_keys.Keys(FocusActions)} 切窗格";
        }
        RenderVersion++;
    }

    /** 窗格尺寸变化时同步给 shell 窗格(PTY 需要按真实行列折行)。 */
    private void SyncShellPaneSizes()
    {
        // pty 层占整屏(不在窗格树的分格里): 按整块正文区给它尺寸, 否则会按分屏的小格子折行。
        if (_ptyLayer is { } layer)
            layer.Resize(Math.Max(1, _paneArea.Width), Math.Max(1, _paneArea.Height - 1));
        foreach (var placement in _paneLayout.Panes)
        {
            if (!_panes.TryGetValue(placement.PaneId, out var pane) || pane is not ShellPane shell || ReferenceEquals(shell, _ptyLayer))
                continue;
            shell.Resize(placement.Rect.Width, Math.Max(1, placement.Rect.Height - 1));
        }
    }

    /**
     * Ctrl+X ,/. : 在"当前窗格所属 Pty"的会话列表里循环切换(tmux 的 window 切换语义), 不按归属分叉:
     * - 本进程的会话 → 聚焦已有窗格, 没有窗格就装进输入窗格并聚焦;
     * - 别的进程(该 pty 的常驻 DSH)的会话 → 请对面把焦点切到该会话的窗格(既有的 focus 控制消息), 本窗格的镜像随之显示它。
     */
    private async void SwitchSessionInPty(int direction)
    {
        try
        {
            var pty = CurrentPtyId();
            var dto = _daemonPtys.FirstOrDefault(candidate => string.Equals(candidate.Id, pty, StringComparison.Ordinal));
            var local = SessionsInPty(pty);
            var remote = RemoteOf(dto).Where(id => local.All(session => !string.Equals(session.Value, id, StringComparison.Ordinal))).ToList();
            var order = new List<string>([.. local.Select(session => session.Value), .. remote]);
            if (order.Count == 0)
            {
                SetFocusedStatus($"pty {pty ?? "(未归属)"} 下没有可切换的会话");
                return;
            }

            if (order.Count == 1)
            {
                FocusOrSwitchSession(pty, order[0]);
                SetFocusedStatus($"pty {pty} 下只有 1 个会话: {order[0]}");
                return;
            }

            var currentId = CurrentSessionId(dto);
            var index = currentId is { Length: > 0 } ? order.FindIndex(id => string.Equals(id, currentId, StringComparison.Ordinal)) : -1;
            var nextIndex = ((index + direction) % order.Count + order.Count) % order.Count;
            var next = order[nextIndex];
            SetFocusedStatus($"切换到会话 {ShortSession(next)} ({nextIndex + 1}/{order.Count}) - pty {pty}");
            await FocusOrSwitchSessionAsync(pty, next);
        }
        catch (Exception error)
        {
            SetFocusedStatus($"切换会话失败: {error.Message}");
        }
    }

    /** 切到某 pty 下的某个会话: 本进程的原生聚焦/装载(并退出 pty 层, 因为会话视图就是那一层的"上一层"); 别的进程的请它聚焦该会话的窗格。 */
    private async Task FocusOrSwitchSessionAsync(string? pty, string sessionId)
    {
        if (SessionsInPty(pty).Any(session => string.Equals(session.Value, sessionId, StringComparison.Ordinal)))
        {
            if (FocusExistingSessionPane(SessionId.Create(sessionId)))
            {
                LeavePtyLayer();
                return;
            }
            // 目标会话没有窗格: 装进输入窗格并聚焦, 这样从"pty 层"回到会话视图是可见的。
            var target = InputPane;
            LeavePtyLayer();
            FocusPane(target.Id);
            await ResumeSession(target, $"/resume {sessionId}");
            return;
        }

        var paneId = _daemonPtys
            .FirstOrDefault(candidate => string.Equals(candidate.Id, pty, StringComparison.Ordinal))?.Panes?
            .FirstOrDefault(pane => string.Equals(pane.SessionId, sessionId, StringComparison.Ordinal))?.Id;
        if (paneId is null || pty is null)
        {
            SetFocusedStatus($"会话 {ShortSession(sessionId)} 没有可聚焦的窗格");
            return;
        }

        // 别的进程的会话: 请对面切过去, 本窗格的镜像随之显示它(留在这一层里)。
        await DaemonPtyAccess.ControlSendAsync(pty, "focus", paneId.Value, "");
    }

    private void FocusOrSwitchSession(string? pty, string sessionId)
        => _ = FocusOrSwitchSessionAsync(pty, sessionId);

    /** 当前焦点窗格所属的 pty: pty 层窗格用它代表的那一层, 会话窗格用归属推导(与总览同源)。 */
    private string? CurrentPtyId()
    {
        if (FocusedPane is ShellPane { LayerPtyId: { Length: > 0 } layer })
            return layer;
        var current = Catalog().FirstOrDefault(entry => entry.Id == _focusedPaneId);
        return current?.SessionId is { Length: > 0 } session ? PtyForSession(session) : current?.PtyId;
    }

    /** 当前"所在会话": 焦点是会话窗格就是它; 在 pty 层里取对面发布的当前窗格所属会话。 */
    private string? CurrentSessionId(PtyDaemonSessionDto? dto)
        => FocusedPane.Session?.Id.Value ?? (dto?.Panes ?? []).FirstOrDefault(pane => pane.Focused)?.SessionId;

    /**
     * 某 pty 下的会话列表(本进程注册的顶层会话, 创建序): 与总览共用 PtyForSession 归属推导。
     * pty 为 null 表示这些会话都没有 pty 归属(例如 TUI 不跑在 daemon pty 里), 此时仍按"同一个 pty"分组切换。
     */
    private List<SessionId> SessionsInPty(string? pty)
    {
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName);
        if (agents is null)
            return [];
        return
        [
            .. agents.Roots()
                .Where(agent => string.Equals(PtyForSession(agent.Id.Value), pty, StringComparison.Ordinal))
                .Select(agent => agent.Id),
        ];
    }

    /** 该会话已有窗格则聚焦并返回 true; 没有窗格返回 false(由调用方决定装载到哪个窗格)。 */
    private bool FocusExistingSessionPane(SessionId sessionId)
    {
        var pane = _panes.Values.FirstOrDefault(candidate => candidate.Session?.Id == sessionId);
        if (pane is null)
            return false;
        FocusPane(pane.Id);
        return true;
    }

    private async void SplitFocusedPane()
    {
        var input = InputPane;
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName);
        if (agents is null)
            return;
        try
        {
            var options = input.Agent.Options;
            var cwd = input.CurrentCwd();
            var handle = await agents.Create(new CreateAgentOptions(
                SessionId.Create($"session-{Guid.NewGuid()}"),
                cwd,
                new AgentOptions(options.Provider, options.Model, options.ReasoningEffort, options.MaxTokens)));
            var newAgent = (AgentLoopAgent)handle.Agent;
            await newAgent.WhenIdle();
            QueueAction(() => AddPane(newAgent, null));
        }
        catch (Exception error)
        {
            input.AppendRaw($"  split failed: {error.Message}\n");
        }
    }

    private void MoveFocus(FocusDirection direction)
    {
        var neighbor = PaneTree.FindNeighbor(_paneLayout, _focusedPaneId, direction);
        if (neighbor is { } id)
            FocusPane(id);
    }

    private ITuiPane? FindPaneBySession(Session session)
    {
        foreach (var pane in _panes.Values)
        {
            if (ReferenceEquals(pane.Session, session))
                return pane;
        }
        return null;
    }

    private ChatPane? FindPaneByAgent(IAgent agent)
        => FindPaneBySession(agent.Session) as ChatPane;

    private ITuiPane? FindPaneBySessionId(string sessionId)
    {
        foreach (var pane in _panes.Values)
        {
            if (string.Equals(pane.Session?.Id.Value, sessionId, StringComparison.OrdinalIgnoreCase))
                return pane;
        }
        return null;
    }

    private bool IsPaneAlive(ChatPane pane)
        => _panes.TryGetValue(pane.Id, out var current) && ReferenceEquals(current, pane);

    private void QueueSessionEvent(ITuiPane pane, SessionEvent sessionEvent)
    {
        lock (_gate)
            _pendingEvents.Enqueue((pane, sessionEvent));
        WakeHook?.Invoke();
    }

    private void ClearExitConfirm()
    {
        if (_exitConfirmAt is null)
            return;
        _exitConfirmAt = null;
        InputPane.RefreshMenuStatus();
    }

    private void OpenOverview()
    {
        RefreshOverviewItems();
        var focusedIndex = _overviewItems.FindIndex(item => item.Kind == OverviewTargetKind.Pane && item.PaneId == _focusedPaneId);
        _overviewIndex = focusedIndex >= 0 ? focusedIndex : FirstSelectableIndex();
        _overviewActive = true;
        _ = RefreshDaemonPtysAsync();
    }

    private int FirstSelectableIndex()
        => Math.Max(0, _overviewItems.FindIndex(item => item.Kind != OverviewTargetKind.None));

    private void HandleOverviewKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _overviewActive = false;
                return;
            case ConsoleKey.UpArrow:
                MoveOverview(-1);
                return;
            case ConsoleKey.DownArrow:
                MoveOverview(1);
                return;
            case ConsoleKey.Enter:
                ActivateOverview();
                return;
        }
    }

    /** 方向键只在可选行(Pty/Pane/Session/RemotePane)之间移动; 纯标题行(None)跳过。 */
    private void MoveOverview(int direction)
    {
        if (_overviewItems.Count == 0)
            return;
        var index = _overviewIndex;
        for (var step = 0; step < _overviewItems.Count; step++)
        {
            index = (index + direction + _overviewItems.Count) % _overviewItems.Count;
            if (_overviewItems[index].Kind != OverviewTargetKind.None)
            {
                _overviewIndex = index;
                return;
            }
        }
    }

    private void ActivateOverview()
    {
        var item = _overviewItems.ElementAtOrDefault(_overviewIndex);
        if (item is null)
            return;
        switch (item.Kind)
        {
            case OverviewTargetKind.Pty when item.PtyId is { } ptyId:
                _overviewActive = false;
                EnterPty(ptyId);
                return;
            case OverviewTargetKind.Pane when item.PaneId is { } paneId:
                _overviewActive = false;
                LeavePtyLayer();
                FocusPane(paneId);
                return;
            case OverviewTargetKind.Session when item.SessionId is { } sessionId:
                _overviewActive = false;
                LeavePtyLayer();
                var existing = FindPaneBySessionId(sessionId);
                if (existing is not null)
                    FocusPane(existing.Id);
                else
                    RunSlashCommand(InputPane, $"/resume {sessionId}");
                return;
            case OverviewTargetKind.RemotePane when item.PtyId is { } ptyId && item.PaneId is { } paneId:
                _overviewActive = false;
                _ = FocusRemotePaneSafeAsync(ptyId, paneId);
                return;
        }
    }

    /**
     * 远程 pty 的会话列表: 由它发布的窗格反推(每个窗格带 SessionId), 加上它上报的输入会话(排最前)。
     * 常驻 TUI 会发布自己全部窗格, 所以这足以列出"该 pty 当前的会话集合"(不算权威: 真权威表要它在 publish 里带上)。
     */
    private static List<string> RemoteSessionsOf(PtyDaemonSessionDto pty)
    {
        var sessions = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (pty.AgentSessionId is { Length: > 0 } own && seen.Add(own))
            sessions.Add(own);
        foreach (var pane in pty.Panes ?? [])
        {
            if (pane.SessionId is { Length: > 0 } sessionId && seen.Add(sessionId))
                sessions.Add(sessionId);
        }
        return sessions;
    }

    /** 激活远程窗格: 通知其所属 TUI 把焦点切到该窗格(方案 §4.4 的 focus 控制消息)。 */
    private static async Task FocusRemotePaneSafeAsync(string ptyId, int paneId)
    {
        try
        {
            await PtyDaemonClient.ControlSendAsync(ptyId, "focus", paneId, "");
        }
        catch (Exception)
        {
            // daemon 未起/远端 pty 已消失: 忽略。
        }
    }

    /** 总览: 单一树 Pty > session > pane(不再分“PTY/会话”两段)。 */
    private void RefreshOverviewItems()
    {
        _overviewItems.Clear();
        var catalog = Catalog();
        var placedPanes = new HashSet<int>();
        var placedSessions = new HashSet<string>(StringComparer.Ordinal);
        // 只列存活 Pty(host 与跨进程同一规则); 已退出(Exited)不出现在总览里。
        var hostPtys = PtyHost.Default.List().Where(pty => pty.Status != PtySessionStatus.Exited).ToList();
        var daemonPtys = _daemonPtys
            .Where(pty => !string.Equals(pty.Status, nameof(PtySessionStatus.Exited), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)?.List() ?? [];

        void AddSessionRow(string sessionId)
        {
            if (!placedSessions.Add(sessionId))
                return;
            var agent = agents.FirstOrDefault(candidate => string.Equals(candidate.Id.Value, sessionId, StringComparison.Ordinal));
            var (provider, model) = agent is null ? (string.Empty, string.Empty) : CurrentModel(agent);
            _overviewItems.Add(new OverviewItem($"  {sessionId} - {provider}/{model}", OverviewTargetKind.Session, null, sessionId));
            AddPaneRows(catalog, placedPanes, 4, entry => string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal));
        }

        void AddRemotePaneRow(PtyPaneSnapshotDto pane, string ptyId)
            => _overviewItems.Add(new OverviewItem(
                $"    pane {pane.Id} - {pane.SessionId ?? pane.Title} (remote)",
                OverviewTargetKind.RemotePane,
                pane.Id,
                null,
                ptyId));

        foreach (var pty in hostPtys)
        {
            _overviewItems.Add(new OverviewItem(
                $"> {pty.Id.Value} - {pty.Command} - {pty.Status.ToString().ToLowerInvariant()}",
                OverviewTargetKind.Pty, null, null, pty.Id.Value));
            foreach (var agent in agents.Where(candidate => string.Equals(PtyForSession(candidate.Id.Value), pty.Id.Value, StringComparison.Ordinal)))
                AddSessionRow(agent.Id.Value);
            AddPaneRows(catalog, placedPanes, 4, entry => string.Equals(entry.PtyId, pty.Id.Value, StringComparison.Ordinal));
        }

        foreach (var pty in daemonPtys)
        {
            var ownership = pty.AgentSessionId is { Length: > 0 } sessionId ? $" - {sessionId}" : "";
            _overviewItems.Add(new OverviewItem(
                $"> {pty.Id} (daemon) - {pty.Command} - {pty.Status}{ownership}",
                OverviewTargetKind.Pty, null, null, pty.Id));
            // 该 daemon pty 托管的会话(三级树的中间层)。常驻 TUI 在别的进程, 本进程只能从它发布的窗格反推:
            // 每个窗格带 SessionId, 加上它上报的输入会话; 属于本进程的会话在下面按本地会话行列出, 不在这里重复。
            foreach (var session in RemoteSessionsOf(pty).Where(session => !agents.Any(agent => string.Equals(agent.Id.Value, session, StringComparison.Ordinal))))
            {
                _overviewItems.Add(new OverviewItem($"  {session}", OverviewTargetKind.None, null, null, pty.Id));
                foreach (var pane in (pty.Panes ?? []).Where(pane => string.Equals(pane.SessionId, session, StringComparison.Ordinal)))
                    AddRemotePaneRow(pane, pty.Id);
            }

            // 归属未知的远程窗格也不能丢。
            foreach (var pane in (pty.Panes ?? []).Where(pane => string.IsNullOrEmpty(pane.SessionId)))
                AddRemotePaneRow(pane, pty.Id);

            // 本进程归属该 daemon pty 的会话, 以及为它开的终端窗格(EnterPty 的窗格级订阅)。
            foreach (var agent in agents.Where(candidate => string.Equals(PtyForSession(candidate.Id.Value), pty.Id, StringComparison.Ordinal)))
                AddSessionRow(agent.Id.Value);
            AddPaneRows(catalog, placedPanes, 4, entry => string.Equals(entry.PtyId, pty.Id, StringComparison.Ordinal));
        }

        // 不归属任何已列 Pty 的会话与窗格: 会话按 id, 窗格统一落入“未分组”。
        foreach (var agent in agents)
            AddSessionRow(agent.Id.Value);
        var ungrouped = catalog.Where(entry => !placedPanes.Contains(entry.Id)).ToList();
        if (ungrouped.Count > 0)
        {
            _overviewItems.Add(new OverviewItem("未分组", OverviewTargetKind.None, null, null));
            foreach (var entry in ungrouped)
                AddPaneRow(placedPanes, entry, 2);
        }

        if (_overviewItems.Count == 0)
            _overviewItems.Add(new OverviewItem("  (无)", OverviewTargetKind.None, null, null));
    }

    /**
     * 进入 Pty = **整屏换到"这个 pty 这一层"**(不是 detach, 也不是新增一个窗格和会话并排):
     * 树是 pty > session > pane, pty 是 session 的上一层容器; 这一层与 tmux 的空 session 同形 ——
     * 内容是一个能跑普通命令(或操作对面程序)的真终端, 同时 Ctrl+X 仍归 DSH。
     * - 本进程 host pty, 或 daemon 里任何别的 pty → 那个 shell / 那个常驻 DSH 的画面(订阅镜像 + 按键写回);
     * - 只有"本 TUI 自己所在的 pty"例外(画面就是眼前这份 UI, 镜像会回环) → 由 DSH 为这一层托一个本地 shell;
     * - 连 shell 都起不来(受限环境 / GPU 模式等) → 内容留空, 但状态行与 Ctrl+X 照常可用。
     * 退出这一层: Ctrl+X - / 在该层里选中一个会话 / 从总览激活会话或窗格。
     */
    private void EnterPty(string ptyId)
    {
        if (_ptyLayer is { } layer && string.Equals(layer.LayerPtyId, ptyId, StringComparison.Ordinal))
            return;
        LeavePtyLayer();

        foreach (var entry in Catalog())
        {
            if (string.Equals(entry.PtyId, ptyId, StringComparison.Ordinal) && _panes.ContainsKey(entry.Id))
            {
                FocusPane(entry.Id);
                return;
            }
        }

        if (PtyHost.Default.Get(ptyId) is { } session && session.Status != PtySessionStatus.Exited)
        {
            AddPtyLayerPane(ptyId, new LocalTerminalBacking(session));
            return;
        }

        if (_daemonPtys.FirstOrDefault(pty => string.Equals(pty.Id, ptyId, StringComparison.Ordinal)) is { } daemonPty)
        {
            // 这一层必须是**终端**: pty 的程序本来就是 shell → 用它(订阅 + 按键写回);
            // 否则(常驻 DSH, 含本 TUI 自己所在的、别人的) → 由 DSH 另托一个本地 shell。
            // 绝不把别人的对话界面镜像进来当"这一层的内容"(那是 session 层, 不是 pty 层)。
            if (PtyShell.IsShellCommand(daemonPty.Command))
                _ = SubscribePtyLayerAsync(ptyId);
            else
                _ = OpenPtyLayerWithHostedShellAsync(ptyId);
            return;
        }

        InputPane.AppendRaw($"  pty {ptyId} 不在 daemon 里, 也无法在本进程进入\n");
    }

    /** 退出 pty 层视图(关掉这一层的窗格: 托管的 shell / 订阅随之释放), 回到会话视图。 */
    internal void LeavePtyLayer()
    {
        if (_ptyLayer is not { } layer)
            return;
        _ptyLayer = null;
        if (_panes.ContainsKey(layer.Id))
            RemovePane(layer.Id);
        RenderVersion++;
    }

    /** 开一个"pty 层"窗格: backing 为 null 表示这一层起不了 shell(只剩状态行, Ctrl+X 仍可用)。 */
    private void AddPtyLayerPane(string ptyId, ITerminalBacking? backing)
    {
        var pane = new ShellPane(this, _nextPaneId++, backing, Math.Max(20, _paneArea.Width), Math.Max(4, _paneArea.Height - 1), ptyId);
        pane.LayerBanner = PtyLayerStatusText(ptyId);
        AddPane(pane, null);
        _ptyLayer = pane;
        RenderVersion++;
    }

    /** daemon 里程序就是 shell 的 pty: 订阅它的字节流, 按键写回那个 shell(进这一层 = 进那个 shell)。 */
    private async Task SubscribePtyLayerAsync(string ptyId)
    {
        try
        {
            var backing = await DaemonPtyAccess.EnterAsync(ptyId, Math.Max(4, _paneArea.Height - 1), Math.Max(20, _paneArea.Width));
            QueueAction(() => AddPtyLayerPane(ptyId, backing));
        }
        catch (Exception error)
        {
            // 订阅不上也仍然给这一层(空白 + 状态行): 至少 Ctrl+X 可用。
            QueueAction(() =>
            {
                InputPane.AppendRaw($"  进入 pty {ptyId} 失败: {error.Message}\n");
                AddPtyLayerPane(ptyId, null);
            });
        }
    }

    /** 程序不是 shell 的 pty(常驻 DSH): 由 DSH 为这一层托一个本地 shell; 起不来就给空白层。 */
    private async Task OpenPtyLayerWithHostedShellAsync(string ptyId)
    {
        ITerminalBacking? backing = null;
        var shell = PtyShell.Resolve();
        if (PtyShell.Exists(shell))
        {
            try
            {
                var session = await PtyHost.Default.StartAsync(new PtyStartInfo
                {
                    FileName = shell,
                    Arguments = PtyShell.Arguments(shell),
                    WorkingDirectory = InputPane.CurrentCwd(),
                    Environment = PtyShell.ChildEnvironment(),
                    Rows = Math.Max(4, _paneArea.Height - 1),
                    Columns = Math.Max(20, _paneArea.Width),
                });
                backing = new LocalTerminalBacking(session);
            }
            catch (Exception)
            {
                // 拉不起 shell: 这一层只给状态行, 不阻塞进入(受限环境/GPU 模式仍需响应 Ctrl+X)。
            }
        }

        QueueAction(() => AddPtyLayerPane(ptyId, backing));
    }

    /** pty 层状态行(底部横幅): 当前在哪一层、该 pty 的会话列表(当前会话带 `*`)、可用快捷键。 */
    private string PtyLayerStatusText(string ptyId)
    {
        var dto = _daemonPtys.FirstOrDefault(pty => string.Equals(pty.Id, ptyId, StringComparison.Ordinal));
        var local = SessionsInPty(ptyId);
        var remote = RemoteOf(dto).Where(id => local.All(session => !string.Equals(session.Value, id, StringComparison.Ordinal))).ToList();
        var order = new List<string>([.. local.Select(session => session.Value), .. remote]);
        var current = CurrentSessionId(dto);
        var list = order.Count == 0
            ? "会话: (无)"
            : "会话: " + string.Join(' ', order.Select((id, index) =>
                $"{index + 1}:{ShortSession(id)}{(string.Equals(id, current, StringComparison.Ordinal) ? "*" : "")}"));
        return $"[pty {ptyId}] 层 - {list} - {_keys.Keys(TuiAction.PrevSession, TuiAction.NextSession)} 切会话 - {_keys.Keys(TuiAction.Overview)} 总览 - {_keys.Keys(TuiAction.Detach)} 离开 DSH";
    }

    /** 该 daemon pty 发布上来的会话 id(没有该 pty 时为空)。 */
    private static List<string> RemoteOf(PtyDaemonSessionDto? pty)
        => pty is null ? [] : RemoteSessionsOf(pty);

    /** 状态行里的会话简写: `session-` 前缀加前 8 位(完整 id 在总览树里)。 */
    private static string ShortSession(string sessionId)
    {
        const int prefix = 8;
        const string name = "session-";
        if (!sessionId.StartsWith(name, StringComparison.Ordinal))
            return sessionId;
        var start = name.Length;
        return sessionId[start..Math.Min(sessionId.Length, start + prefix)];
    }

    /** 本 TUI 所在的 pty id(daemon 启动子进程时注入): 会话兜底归属与"是否是自己这层"共用同一真相。 */
    private static string? ResidentPtyId
        => Environment.GetEnvironmentVariable(PtySessionProtocol.SessionVariable) is { Length: > 0 } resident
            ? resident
            : null;

    /** 会话所属 pty: daemon 上报的归属优先(跨进程), 否则常驻进程自身所在的 pty; 都没有返回 null。 */
    private string? PtyForSession(string sessionId)
    {
        foreach (var pty in _daemonPtys)
        {
            if (string.Equals(pty.AgentSessionId, sessionId, StringComparison.Ordinal))
                return pty.Id;
        }
        return ResidentPtyId;
    }

    /** 窗格目录(按 pane 树顺序): 总览与 pane_list/pane_read 共用同一份归属推导(CatalogEntryOf), 避免两套真相。 */
    private IReadOnlyList<PaneCatalogEntry> Catalog()
    {
        var entries = new List<PaneCatalogEntry>(_panes.Count);
        foreach (var id in PaneTree.PaneIds(_paneTree))
        {
            // pty 层窗格是"一层"而不是会话里的窗格: 不进窗格目录(总览树/pane_list 都不该把它当 pane)。
            if (_panes.TryGetValue(id, out var pane) && pane is not ShellPane { LayerPtyId: { Length: > 0 } })
                entries.Add(CatalogEntryOf(pane));
        }
        return entries;
    }

    private void AddPaneRows(IReadOnlyList<PaneCatalogEntry> catalog, HashSet<int> placed, int indent, Func<PaneCatalogEntry, bool> match)
    {
        foreach (var entry in catalog)
        {
            if (!placed.Contains(entry.Id) && match(entry))
                AddPaneRow(placed, entry, indent);
        }
    }

    private void AddPaneRow(HashSet<int> placed, PaneCatalogEntry entry, int indent)
    {
        var marker = entry.Focused ? "* " : "  ";
        var target = entry.SessionId ?? entry.Title;
        _overviewItems.Add(new OverviewItem(
            $"{new string(' ', indent)}{marker}pane {entry.Id} - {target}",
            OverviewTargetKind.Pane,
            entry.Id,
            null));
        placed.Add(entry.Id);
    }

    internal async Task<IReadOnlyList<PtyDaemonSessionDto>> RefreshDaemonPtysAsync()
    {
        try
        {
            var sessions = await DaemonPtyAccess.ListAsync();
            QueueAction(() =>
            {
                _daemonPtys = sessions;
                if (_overviewActive)
                    RefreshOverviewItems();
            });
            return sessions;
        }
        catch (Exception error)
        {
            _ctx.LoggerFor("tui").Debug($"pty daemon list failed: {error.Message}");
            return [];
        }
    }

    private void DrawOverview(CellGrid grid)
    {
        var area = OverlayArea(grid);
        var lines = _overviewItems.Select(item => item.Line).ToList();
        PopupList.Draw(grid, area, "总览 Pty > 会话 > 窗格", lines, _overviewIndex);
    }

    private void DrawAgentList(CellGrid grid)
    {
        var area = OverlayArea(grid);
        var lines = _agentItems.Select(AgentLine).ToList();
        PopupList.Draw(grid, area, $"子代理 ({_keys.Keys(TuiAction.AgentList)})", lines, _agentsIndex);
    }

    private ConsoleRect OverlayArea(CellGrid grid)
        => _overlayArea.Height > 0
            ? _overlayArea
            : new ConsoleRect(0, 0, grid.Width, Math.Max(3, grid.Height - 3));

    private string AgentLine(SubagentNode node)
    {
        var indent = new string(' ', Math.Max(0, node.Depth - 1) * 2);
        var state = IsLive(node.Id) ? "* live" : "o ended";
        var suffix = node.HasChildren ? " +" : "";
        return $"{indent}{state} - {node.Label ?? node.Id.Value} [{node.Mode}]{suffix}";
    }

    private void OpenAgentList()
    {
        RefreshAgentItems();
        _agentsIndex = 0;
        _agentsActive = true;
    }

    private void HandleAgentListKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _agentsActive = false;
                return;
            case ConsoleKey.UpArrow:
                _agentsIndex = Math.Clamp(_agentsIndex - 1, 0, Math.Max(0, _agentItems.Count - 1));
                return;
            case ConsoleKey.DownArrow:
                _agentsIndex = Math.Clamp(_agentsIndex + 1, 0, Math.Max(0, _agentItems.Count - 1));
                return;
            case ConsoleKey.Enter:
                if (_agentItems.ElementAtOrDefault(_agentsIndex) is { } node)
                    OpenSubagentView(node.Id);
                return;
        }
    }

    private void RefreshAgentsIfActive()
    {
        if (_agentsActive)
            RefreshAgentItems();
    }

    private void RefreshAgentItems()
    {
        _agentItems.Clear();
        _agentItems.AddRange(_subagents.Descendants(InputPane.Agent.Id));
        _agentsIndex = Math.Clamp(_agentsIndex, 0, Math.Max(0, _agentItems.Count - 1));
    }

    internal bool IsLive(SessionId id)
    {
        lock (_gate)
        {
            if (_liveSubagents.Contains(id))
                return true;
        }
        return _subagents.IsLive(id);
    }

    private void DrawPanes(CellGrid grid)
    {
        foreach (var placement in _paneLayout.Panes)
        {
            if (!_panes.TryGetValue(placement.PaneId, out var pane))
                continue;
            var rect = placement.Rect;
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;
            DrawPaneHeader(grid, pane, rect, placement.PaneId == _focusedPaneId);
            if (rect.Height > 1)
                pane.DrawTranscript(grid, new ConsoleRect(rect.X, rect.Y + 1, rect.Width, rect.Height - 1));
        }
        DrawPaneDividers(grid, _paneLayout);
    }

    private static void DrawPaneHeader(CellGrid grid, ITuiPane pane, ConsoleRect rect, bool focused)
    {
        var prefix = focused ? "> " : "  ";
        var style = focused ? CellStyle.Bold : CellStyle.Dim;
        CellText.Draw(grid, rect.X, rect.Y, prefix, focused ? TuiTheme.Arrow : CellColor.Default, AnsiColor.Default, style);
        CellText.Draw(
            grid,
            rect.X + prefix.Length,
            rect.Y,
            pane.PaneTitle,
            focused ? TuiTheme.Highlight : CellColor.Default,
            AnsiColor.Default,
            style);
    }

    private void DrawStatusLine(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        var text = FocusedPane.StatusText;
        // pty 层占整屏, 不存在"第 N/M 个窗格"的说法(那一层不是会话里的窗格)。
        if (_panes.Count > 1 && _ptyLayer is null)
        {
            var order = PaneTree.PaneIds(_paneTree);
            var index = 0;
            for (var position = 0; position < order.Count; position++)
            {
                if (order[position] == _focusedPaneId)
                {
                    index = position;
                    break;
                }
            }
            text = $"[窗格 {index + 1}/{order.Count}] {text}";
        }
        CellText.Draw(grid, rect.X, rect.Y, text, TuiTheme.Status, AnsiColor.Default, CellStyle.Dim);
    }

    private void DrawDividers(CellGrid grid, UiLayout layout)
    {
        var verticalX = RightPanelDividerColumn(layout);
        DrawHorizontalDivider(grid, layout.Input.Y - 1, verticalX, layout.Main.Bottom, layout.Input.Y);
        DrawHorizontalDivider(grid, layout.Status.Y - 1, verticalX, layout.Input.Bottom, layout.Status.Y);
        if (verticalX < 0 || layout.Main.Height <= 0)
            return;
        for (var y = layout.Main.Y; y < layout.Main.Bottom && y < grid.Height; y++)
            grid[verticalX, y] = new Cell('│', TuiTheme.Divider, AnsiColor.Default, CellStyle.Dim);
    }

    private static void DrawHorizontalDivider(CellGrid grid, int y, int verticalX, int minimumY, int maximumY)
    {
        if (y < minimumY || y >= maximumY || y < 0 || y >= grid.Height)
            return;
        for (var x = 0; x < grid.Width; x++)
            grid[x, y] = new Cell(x == verticalX ? '┼' : '─', TuiTheme.Divider, AnsiColor.Default, CellStyle.Dim);
    }

    private static void DrawPaneDividers(CellGrid grid, PaneLayout layout)
    {
        List<SplitDivider> verticals = [];
        List<SplitDivider> horizontals = [];
        foreach (var divider in layout.Dividers)
            (divider.Orientation == SplitOrientation.Vertical ? verticals : horizontals).Add(divider);

        foreach (var divider in verticals)
        {
            if (divider.X < 0 || divider.X >= grid.Width)
                continue;
            for (var y = divider.Y; y < divider.Y + divider.Length && y < grid.Height; y++)
            {
                if (y >= 0)
                    grid[divider.X, y] = new Cell('│', TuiTheme.Divider, AnsiColor.Default, CellStyle.Dim);
            }
        }

        foreach (var divider in horizontals)
        {
            if (divider.Y < 0 || divider.Y >= grid.Height)
                continue;
            for (var x = divider.X; x < divider.X + divider.Length && x < grid.Width; x++)
            {
                if (x >= 0)
                    grid[x, divider.Y] = new Cell('─', TuiTheme.Divider, AnsiColor.Default, CellStyle.Dim);
            }
        }

        foreach (var vertical in verticals)
        {
            foreach (var horizontal in horizontals)
            {
                var x = vertical.X;
                var y = horizontal.Y;
                if (x >= horizontal.X && x < horizontal.X + horizontal.Length
                    && y >= vertical.Y && y < vertical.Y + vertical.Length
                    && x >= 0 && x < grid.Width && y >= 0 && y < grid.Height)
                {
                    grid[x, y] = new Cell('┼', TuiTheme.Divider, AnsiColor.Default, CellStyle.Dim);
                }
            }
        }
    }

    private static (string? Provider, string? Model) CurrentModel(IAgent agent)
    {
        var config = agent.Session.RequestHeader()?.Config;
        return (config?.Provider ?? agent.Options.Provider, config?.Model ?? agent.Options.Model);
    }

    private void LoadSkillCandidates()
    {
        var catalog = _ctx.Get<ISkillCatalog>(ISkillCatalog.ServiceName, false);
        if (catalog is null)
            return;
        _ = LoadSkillCandidatesAsync(catalog);
    }

    /** 先读缓存(菜单立即可用), 过期再后台刷新; 失败只记日志, 候选退回缓存内容。 */
    private void LoadProviderCatalogCandidates()
    {
        _providerCatalog = ProviderCatalog.LoadCached(_home);
        _providerCatalogCandidates = CatalogCandidates(_providerCatalog);
        _providerCatalogRefresh = RefreshProviderCatalogCandidatesAsync(_lifetime.Token);
    }

    private async Task RefreshProviderCatalogCandidatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await ProviderCatalog.LoadAsync(_home, OutboundProxy.FromContext(_ctx), refresh: false, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return;
            _providerCatalog = snapshot;
            _providerCatalogCandidates = CatalogCandidates(snapshot);
            if (snapshot.Error is { Length: > 0 } error)
                _ctx.LoggerFor("tui").Warn($"provider catalog: {error}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _ctx.LoggerFor("tui").Warn($"failed to refresh provider catalog: {error.Message}");
        }
    }

    /** 目录里的一条 provider(按 id 查): `/provider add` 选中它后自动带出 baseUrl/type/models。 */
    internal ProviderCatalogEntry? FindCatalogProvider(string id)
        => _providerCatalog?.Providers.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> CatalogCandidates(ProviderCatalogSnapshot snapshot)
        => [.. snapshot.Providers.Where(provider => provider.Type is not null).Select(provider => provider.Id)];

    private async Task LoadSkillCandidatesAsync(ISkillCatalog catalog)
    {
        try
        {
            _skillCandidates = await catalog.ListNames();
        }
        catch (Exception error)
        {
            _ctx.LoggerFor("tui").Warn($"failed to load skill candidates: {error.Message}");
        }
    }

    private enum OverviewTargetKind
    {
        None,
        Pty,
        Pane,
        RemotePane,
        Session,
    }

    private sealed record OverviewItem(string Line, OverviewTargetKind Kind, int? PaneId, string? SessionId, string? PtyId = null);
}

