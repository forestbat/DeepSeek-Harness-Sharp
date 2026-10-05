using System.Diagnostics;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Pty;
using Dsh.Subagent;
using Dsh.Tui.Services;

namespace Dsh.Tui;

/**
 * TUI 外壳: 持有 pane 集合与二叉分割树, 把键盘/鼠标路由到焦点 pane, 并处理 Ctrl+X 全局前缀。
 * 单 pane 时绘制路径与历史行为一致 (Main + RightPanel + Input + Status)。
 * Ctrl+X 组合键走 TerminalRawMode 的进程级原始输入捕获, 不经过宿主编辑控件, 因此 Windows 上不会触发"剪切"。
 */
public sealed class ChatWindow : IDisposable
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
    private IReadOnlyList<string> _providerCatalogCandidates = [];
    private ProviderCatalogSnapshot? _providerCatalog;
    private IReadOnlyList<string>? _mcpPanelLines;
    private IReadOnlyList<PtyDaemonSessionDto> _daemonPtys = [];
    private PaneNode _paneTree = new PaneLeaf(0);
    private PaneLayout _paneLayout;
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
    private bool _ctrlXPrefix;
    private readonly Func<bool> _unsubscribe;
    private readonly Func<bool> _approvalSubscription;
    private readonly Func<bool> _questionsSubscription;
    private readonly Func<bool> _skillChangeSubscription;
    private readonly Func<bool> _subagentStartSubscription;
    private readonly Func<bool> _subagentEndSubscription;

    public ChatWindow(Context ctx, AgentLoopAgent agent, HarnessHome home, ISessionPersistence? persistence = null, TuiSettings? settings = null)
    {
        _ctx = ctx;
        _home = home;
        _persistence = persistence;
        _settings = settings;
        _rightPanelWidth = settings?.SidebarWidth;
        _inputHeight = settings?.InputHeight;
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

        _approvalSubscription = ctx.OnWaterfall<ApprovalRequestNotification>((notification, _) =>
        {
            var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueAction(() =>
            {
                var target = FindPaneByAgent(notification.Request.Agent) ?? InputPane;
                target.ShowApprovalPrompt(notification.Request, answer);
            });
            return new ValueTask<object?>(answer.Task);
        }, new EventOptions { Global = true });

        _questionsSubscription = ctx.OnWaterfall<UserQuestionsRequestNotification>((notification, _) =>
        {
            var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueAction(() =>
            {
                var requested = notification.Request.Agent;
                var target = (requested is not null ? FindPaneByAgent(requested) : null) ?? InputPane;
                target.ShowQuestionPrompt(notification.Request, answer);
            });
            return new ValueTask<object?>(answer.Task);
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

    internal HarnessHome Home => _home;

    internal IReadOnlyList<string> SkillCandidates => _skillCandidates;

    /** models.dev 目录里当前有适配器可服务的 provider 名(`/provider add` 的候选)。 */
    internal IReadOnlyList<string> ProviderCatalogCandidates => _providerCatalogCandidates;

    internal SubagentDirectory Subagents => _subagents;

    internal int PaneCount => _panes.Count;

    internal int FocusedPaneId => _focusedPaneId;

    internal int InputPaneId => _inputPaneId;

    internal bool IsFocusedPaneSubagent => FocusedPane is SubagentPane;

    internal SessionId? FocusedSubagentSessionId => (FocusedPane as SubagentPane)?.Session.Id;

    internal int SubagentPaneCount => _panes.Values.OfType<SubagentPane>().Count();

    internal ChatPane PaneById(int id) => (ChatPane)_panes[id];

    private ChatPane InputPane => (ChatPane)_panes[_inputPaneId];

    private ITuiPane FocusedPane => _panes[_focusedPaneId];

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
        InputPane.HandlePaste(text);
    }

    public void HandleKey(ConsoleKeyInfo key)
    {
        RenderVersion++;
        // 无键名也无字符的事件(纯修饰键按下等)不携带任何意图, 放行会误消费 Ctrl+X 前缀与审批态。
        if (key.Key == ConsoleKey.NoName && key.KeyChar == '\0')
            return;
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

        // 焦点在 shell 窗格: 按键直达 PTY(含 Ctrl+C 交给 shell), 只有 Ctrl+X 前缀仍归窗口。
        var shell = FocusedPane as ShellPane;
        if (shell is not null)
        {
            if (_ctrlXPrefix)
            {
                _ctrlXPrefix = false;
                HandleCtrlXKey(key);
                return;
            }
            if (key.Key == ConsoleKey.X && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                _ctrlXPrefix = true;
                SetCtrlXHint("Ctrl+X: N 新会话 · S 会话 · D detach · A 子代理 · W 总览 · T 开 shell · + 分屏 · - 关窗格 · Q 退出");
                return;
            }
            shell.HandleKey(key);
            return;
        }

        var input = InputPane;
        if (input.PendingApproval is not null || input.PendingQuestions is not null)
        {
            input.HandleKey(key);
            return;
        }

        if (_ctrlXPrefix)
        {
            _ctrlXPrefix = false;
            HandleCtrlXKey(key);
            return;
        }

        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            switch (key.Key)
            {
                case ConsoleKey.X:
                    _ctrlXPrefix = true;
                    SetCtrlXHint("Ctrl+X: N 新会话 · S 会话 · D detach · K 删会话 · A 子代理 · W 总览 · T 开 shell · + 分屏 · - 关窗格 · 方向键/O 切窗格 · Q 退出");
                    return;
                case ConsoleKey.C:
                    if (input.Busy)
                    {
                        ClearExitConfirm();
                        input.Agent.Cancel(new AgentCancelCause.User());
                        return;
                    }
                    if (_exitConfirmAt is { } firstPress
                        && Environment.TickCount64 - firstPress <= ExitConfirmWindowMs)
                    {
                        _exitConfirmAt = null;
                        RequestExit();
                        return;
                    }
                    _exitConfirmAt = Environment.TickCount64;
                    input.StatusText = "再按一次 Ctrl+C 退出";
                    return;
                case ConsoleKey.P:
                    ClearExitConfirm();
                    input.OpenPromptCommandMenu();
                    return;
                default:
                    ClearExitConfirm();
                    break;
            }
        }
        else
        {
            ClearExitConfirm();
        }

        if (FocusedPane is SubagentPane subagent)
        {
            if (subagent.HandleKey(key))
                return;
        }

        input.HandleKey(key);
    }

    private void HandleCtrlXKey(ConsoleKeyInfo key)
    {
        var input = InputPane;
        key = NormalizeSubcommandKey(key);
        switch (key.Key)
        {
            case ConsoleKey.N:
                RunSlashCommand(input, "/new");
                return;
            case ConsoleKey.S:
                input.Input = "/session ";
                input.Cursor = input.Input.Length;
                input.RefreshMenus();
                return;
            case ConsoleKey.D:
                RequestDetach(input);
                return;
            case ConsoleKey.K:
                input.Input = "/session delete ";
                input.Cursor = input.Input.Length;
                input.RefreshMenus();
                return;
            case ConsoleKey.A:
                OpenAgentList();
                return;
            case ConsoleKey.W:
                OpenOverview();
                return;
            case ConsoleKey.T:
                AddShellPane();
                return;
            case ConsoleKey.Q:
                RequestExit();
                return;
            case ConsoleKey.OemPlus or ConsoleKey.Add:
                SplitFocusedPane();
                return;
            case ConsoleKey.OemMinus or ConsoleKey.Subtract:
                CloseFocusedPane();
                return;
            case ConsoleKey.LeftArrow:
                MoveFocus(FocusDirection.Left);
                return;
            case ConsoleKey.RightArrow:
                MoveFocus(FocusDirection.Right);
                return;
            case ConsoleKey.UpArrow:
                MoveFocus(FocusDirection.Up);
                return;
            case ConsoleKey.DownArrow:
                MoveFocus(FocusDirection.Down);
                return;
            case ConsoleKey.O:
                FocusPane(PaneTree.NextPane(_paneLayout, _focusedPaneId));
                return;
        }

        input.RefreshMenuStatus();
    }

    private void SetCtrlXHint(string hint)
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
        FocusedPane.HandleMouseWheel(delta);
    }

    /** 鼠标滚轮按指针所在 pane 路由; 无命中时回退焦点 pane。 */
    public void HandleMouseWheel(float delta, int cellX, int cellY, UiLayout layout)
    {
        RenderVersion++;
        layout = Effective(layout);
        // 命令浮层打开时: 正文区滚轮滚动候选(与 ↑/↓ 等价), 便于浏览长命令列表。
        if (layout.Main.Contains(cellX, cellY) && InputPane.ScrollOverlay(delta))
            return;
        // 单窗格时指针落在右栏上: 滚右栏内容(窄终端里才看得到下面的段落), 不再滚正文。
        if (_panes.Count == 1 && layout.RightPanel.Contains(cellX, cellY) && InputPane.ScrollRightPanel(delta))
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
        layout = Effective(layout);
        var input = InputPane;
        if (input.PendingApproval is not null)
            return;
        if (input.TryBeginMentionScrollDrag(cellX, cellY))
            return;
        if (_panes.Count > 1 && FindDividerAt(cellX, cellY) is { } divider)
        {
            _dividerDrag = divider;
            return;
        }

        // 单窗格: 右栏竖线(拖宽度)和输入栏上沿(拖高度)都可拖(以前只有多窗格的窗格树分割线参与拖动)。
        var dividerColumn = _panes.Count == 1 ? RightPanelDividerColumn(layout) : -1;
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

    /** 指针拖动: 分隔线上按下则调整窗格比例, 否则扩展文本选择(阶段 3.2)。 */
    public void HandleMouseDrag(int cellX, int cellY, UiLayout layout)
    {
        RenderVersion++;
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
        _paneArea = new ConsoleRect(0, 0, grid.Width, Math.Max(0, layout.Main.Height));
        // 浮层(总览/子代理列表)的正文区域: 单窗格且有右侧栏时限于正文宽, 避免居中基准包含侧栏而压到侧栏上
        _overlayArea = _panes.Count == 1 && layout.RightPanel.Width > 0 ? layout.Main : _paneArea;
        _paneLayout = LayoutEngine.EvaluatePanes(_paneTree, _paneArea);
        SyncShellPaneSizes();

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
        }

        InputPane.DrawInput(grid, layout.Input);
        DrawStatusLine(grid, layout.Status);
        DrawDividers(grid, layout);
        if (_overviewActive)
            DrawOverview(grid);
        if (_agentsActive)
            DrawAgentList(grid);
        DrawPaneOverlays(grid, layout);
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
        _paneTools?.Dispose();
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
                entries.Add(CatalogEntryOf(pane));
            return (IReadOnlyList<PaneCatalogEntry>)entries;
        });

    /** 读取工具挂载: 交互形态由 TuiRunner 调用一次, 随 ChatWindow.Dispose 一并注销。 */
    internal void RegisterPaneTools()
        => _paneTools ??= PaneReadTools.Apply(_ctx, this);

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
        var renderer = new TranscriptRenderer();
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
            var info = new MentionSessionInfo(agent.Id.ToString(), agent.Session.Header.Title, null);
            if (seen.Add(info.Id))
                result.Add(info);
        }

        _sessionInfos = result;
        return result;
    }

    internal void InvalidateSessionInfos()
        => _sessionInfos = null;

    private const int GitRefreshMinIntervalMs = 1500;
    private const string EmptyGitTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private IReadOnlyList<string>? _gitLines;
    private string? _gitCwd;
    private long _gitRefreshedAt;
    private int _gitRefreshing;

    /** 右栏 Git 变更: 只读缓存(未就绪显示"计算中…"), 后台刷新, 不阻塞绘制/GPU 帧路径。 */
    internal IReadOnlyList<string> GitLines()
    {
        var cwd = InputPane.Agent.Session.Header.Cwd;
        if (string.IsNullOrWhiteSpace(cwd))
            return ["(无工作目录)"];
        var stale = Environment.TickCount64 - _gitRefreshedAt >= GitRefreshMinIntervalMs;
        if ((_gitLines is null || !string.Equals(_gitCwd, cwd, StringComparison.Ordinal) || stale)
            && Interlocked.CompareExchange(ref _gitRefreshing, 1, 0) == 0)
            _ = RefreshGitLinesAsync(cwd);
        return _gitLines ?? ["计算中…"];
    }

    private async Task RefreshGitLinesAsync(string cwd)
    {
        try
        {
            var lines = await Task.Run(() => LoadGitLines(cwd));
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

    private static IReadOnlyList<string> LoadGitLines(string cwd)
    {
        var status = RunGit(cwd, "status", "--porcelain");
        if (status is null)
            return ["(非 git 仓库或 git 不可用)"];
        if (status.Trim().Length > 0)
            return GitPanel.Format(RunGit(cwd, "diff", "--numstat", "HEAD") ?? "", "未提交改动");
        var last = RunGit(cwd, "diff", "--numstat", "HEAD~1", "HEAD")
            ?? RunGit(cwd, "diff", "--numstat", EmptyGitTree, "HEAD")
            ?? "";
        return last.Trim().Length == 0 ? ["(无变更)"] : GitPanel.Format(last, "上次提交");
    }

    private static string? RunGit(string cwd, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(cwd);
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("core.quotepath=false");
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
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
        foreach (var local in LocalCommands)
        {
            if (!string.Equals(verb, local.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            await local.Run(pane, text);
            return;
        }
        await RunRegisteredCommandAsync(pane, text);
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
                pane.AppendRaw($"  session cannot be loaded: {raw} — {error.Message}\n");
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
            pane.AppendRaw("  usage: /gpu <number|name> — takes effect after restart\n");
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
        pane.AppendRaw($"  gpu: selected {(selected == GpuCatalog.AutoAdapter ? "auto (system default)" : selected)} — takes effect after restart\n");
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
            pane.AppendRaw("  detached: 会话继续在 daemon 里运行(`dsh tui attach` 可接回)\n");
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
        // 常驻会话: 不带参数的 /detach 与 Ctrl+X D 同义 —— 只放 proxy 走, 自己继续跑。
        if (commandLine.Length == 0 && IsResidentChild())
        {
            RequestDetach(pane);
            return;
        }

        var parts = commandLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        // 缺省: 把**当前这个 TUI 会话**交给 daemon 里的一个新 TUI 继续跑(等价 tmux 的 detach/attach 语义),
        // 这样 attach 回来看到的是完整 TUI 画面(侧栏/输入行/状态行), 而不是一个空 shell;
        // 显式 `/detach <cmd> ...` 仍按命令起一个普通会话。
        var executable = Environment.ProcessPath;
        var isDotnetHost = executable is not null
            && string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
        var fileName = parts.Length > 0 ? parts[0] : executable ?? PtyShell.Resolve();
        var arguments = parts.Length > 0
            ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : isDotnetHost
                ? [Environment.GetCommandLineArgs()[0], "tui", "--session", pane.Agent.Id.ToString(), "--home", _home.Root]
                : ["tui", "--session", pane.Agent.Id.ToString(), "--home", _home.Root];
        // 不带命令 = 起的还是 TUI 本体, 它要鼠标(拖动分隔线/滚轮); 带命令 = 任意命令/普通 shell, 不能打开
        // 宿主终端上报(Unix 上代理原样透传, 报文会被当成命令敲进那个程序)。
        var wantsMouse = parts.Length == 0;

        try
        {
            await PtyDaemonClient.EnsureRunningAsync();
            var session = await PtyDaemonClient.StartAsync(new PtyDaemonStartParams
            {
                FileName = fileName,
                Arguments = [.. arguments],
                WorkingDirectory = pane.CurrentCwd(),
                Rows = Math.Max(4, _paneArea.Height),
                Columns = Math.Max(20, _paneArea.Width),
                Environment = new Dictionary<string, string?>
                {
                    ["DSH_DETACHED_SESSION_ID"] = pane.Agent.Id.ToString(),
                    [PtySessionProtocol.ChildVariable] = "1",
                    [TuiRunner.DetachDraftVariable] = string.IsNullOrEmpty(pane.Input) ? null : pane.Input,
                },
                WantsMouse = wantsMouse,
            });
            pane.AppendRaw($"  detached: {session.Id} — {session.Command} (daemon PTY)\n");
            RequestExit();
        }
        catch (Exception error)
        {
            pane.AppendRaw($"  detach failed: {error.Message}\n");
        }
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
            QueueAction(() => AddPane(new ShellPane(this, id, session, columns, rows), null));
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
            shell.StatusText = "shell 窗格: 按键直达 shell · Ctrl+X - 关闭 · Ctrl+X 方向键/O 切窗格";
        RenderVersion++;
    }

    /** 窗格尺寸变化时同步给 shell 窗格(PTY 需要按真实行列折行)。 */
    private void SyncShellPaneSizes()
    {
        foreach (var placement in _paneLayout.Panes)
        {
            if (!_panes.TryGetValue(placement.PaneId, out var pane) || pane is not ShellPane shell)
                continue;
            shell.Resize(placement.Rect.Width, Math.Max(1, placement.Rect.Height - 1));
        }
    }

    /**
     * Ctrl+X 子命令按键归一: Unix/pty 宿主对 '+'/'-' 只给字符、不上报 OemPlus/OemMinus(Windows conhost 才会),
     * 统一按字符补齐 ConsoleKey, 保证分屏/关窗格在两类宿主上行为一致。
     */
    private static ConsoleKeyInfo NormalizeSubcommandKey(ConsoleKeyInfo key)
    {
        var target = key.KeyChar switch
        {
            '+' => ConsoleKey.OemPlus,
            '-' => ConsoleKey.OemMinus,
            _ => (ConsoleKey?)null,
        };
        if (target is null || key.Key == target)
            return key;
        return new ConsoleKeyInfo(
            key.KeyChar,
            target.Value,
            (key.Modifiers & ConsoleModifiers.Shift) != 0,
            (key.Modifiers & ConsoleModifiers.Alt) != 0,
            (key.Modifiers & ConsoleModifiers.Control) != 0);
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

    /** 方向键只在可选行(Pane/Session)之间移动; PTY 行与分节标题(None)跳过。 */
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
            case OverviewTargetKind.Pane when item.PaneId is { } paneId:
                _overviewActive = false;
                FocusPane(paneId);
                return;
            case OverviewTargetKind.Session when item.SessionId is { } sessionId:
                _overviewActive = false;
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

    private void RefreshOverviewItems()
    {
        _overviewItems.Clear();
        var catalog = Catalog();
        var placed = new HashSet<int>();

        _overviewItems.Add(new OverviewItem("PTY", OverviewTargetKind.None, null, null));
        // 只列存活 Pty(host 与跨进程同一规则); 已退出(Exited)不出现在总览里。
        var hostPtys = PtyHost.Default.List().Where(pty => pty.Status != PtySessionStatus.Exited).ToList();
        var daemonPtys = _daemonPtys
            .Where(pty => !string.Equals(pty.Status, nameof(PtySessionStatus.Exited), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (hostPtys.Count == 0 && daemonPtys.Count == 0)
            _overviewItems.Add(new OverviewItem("  (无)", OverviewTargetKind.None, null, null));
        foreach (var pty in hostPtys)
        {
            _overviewItems.Add(new OverviewItem($"  {pty.Id.Value} · {pty.Command} · {pty.Status.ToString().ToLowerInvariant()}", OverviewTargetKind.None, null, null));
            AddPaneRows(catalog, placed, 4, entry => string.Equals(entry.PtyId, pty.Id.Value, StringComparison.Ordinal));
        }
        foreach (var pty in daemonPtys)
        {
            var ownership = pty.AgentSessionId is { Length: > 0 } sessionId ? $" · {sessionId}" : "";
            _overviewItems.Add(new OverviewItem($"  {pty.Id} (daemon) · {pty.Command} · {pty.Status}{ownership}", OverviewTargetKind.None, null, null));
            foreach (var pane in pty.Panes ?? [])
                _overviewItems.Add(new OverviewItem($"    pane {pane.Id} · {pane.SessionId ?? pane.Title} (remote)", OverviewTargetKind.RemotePane, pane.Id, null, pty.Id));
        }

        _overviewItems.Add(new OverviewItem("会话", OverviewTargetKind.None, null, null));
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)?.List() ?? [];
        if (agents.Count == 0)
            _overviewItems.Add(new OverviewItem("  (无)", OverviewTargetKind.None, null, null));
        foreach (var agent in agents)
        {
            var (provider, model) = CurrentModel(agent);
            var ptyId = PtyForSession(agent.Id.Value);
            _overviewItems.Add(new OverviewItem(
                $"  {agent.Id} · {provider}/{model}",
                OverviewTargetKind.Session,
                null,
                agent.Id.ToString()));
            // 会话 id 很长, pty 放行尾会被浮层宽度截掉; 单独一行保证可见。
            _overviewItems.Add(new OverviewItem(
                ptyId is { Length: > 0 } ? $"    pty {ptyId}" : "    pty (none)",
                OverviewTargetKind.None,
                null,
                null));
            AddPaneRows(catalog, placed, 4, entry => string.Equals(entry.SessionId, agent.Id.Value, StringComparison.Ordinal));
        }

        // 不属于任何已列 Pty/会话的窗格(如子代理窗格)统一放"未分组"。
        var ungrouped = catalog.Where(entry => !placed.Contains(entry.Id)).ToList();
        if (ungrouped.Count > 0)
        {
            _overviewItems.Add(new OverviewItem("未分组", OverviewTargetKind.None, null, null));
            foreach (var entry in ungrouped)
                AddPaneRow(placed, entry, 2);
        }
    }

    /** 会话所属 pty: daemon 上报的归属优先(跨进程), 否则常驻进程自身所在的 pty; 都没有返回 null。 */
    private string? PtyForSession(string sessionId)
    {
        foreach (var pty in _daemonPtys)
        {
            if (string.Equals(pty.AgentSessionId, sessionId, StringComparison.Ordinal))
                return pty.Id;
        }
        var resident = Environment.GetEnvironmentVariable(PtySessionProtocol.SessionVariable);
        return resident is { Length: > 0 } ? resident : null;
    }

    /** 窗格目录(按 pane 树顺序): 总览与 pane_list/pane_read 共用同一份归属推导(CatalogEntryOf), 避免两套真相。 */
    private IReadOnlyList<PaneCatalogEntry> Catalog()
    {
        var entries = new List<PaneCatalogEntry>(_panes.Count);
        foreach (var id in PaneTree.PaneIds(_paneTree))
        {
            if (_panes.TryGetValue(id, out var pane))
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
            $"{new string(' ', indent)}{marker}pane {entry.Id} · {target}",
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
        PopupList.Draw(grid, area, "总览 PTY / 会话 / 窗格", lines, _overviewIndex);
    }

    private void DrawAgentList(CellGrid grid)
    {
        var area = OverlayArea(grid);
        var lines = _agentItems.Select(AgentLine).ToList();
        PopupList.Draw(grid, area, "子代理 (Ctrl+X A)", lines, _agentsIndex);
    }

    private ConsoleRect OverlayArea(CellGrid grid)
        => _overlayArea.Height > 0
            ? _overlayArea
            : new ConsoleRect(0, 0, grid.Width, Math.Max(3, grid.Height - 3));

    private string AgentLine(SubagentNode node)
    {
        var indent = new string(' ', Math.Max(0, node.Depth - 1) * 2);
        var state = IsLive(node.Id) ? "● live" : "○ ended";
        var suffix = node.HasChildren ? " +" : "";
        return $"{indent}{state} · {node.Label ?? node.Id.Value} [{node.Mode}]{suffix}";
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
        var prefix = focused ? "▶ " : "  ";
        CellText.Draw(
            grid,
            rect.X,
            rect.Y,
            prefix + pane.PaneTitle,
            focused ? AnsiColor.BrightCyan : AnsiColor.Default,
            AnsiColor.Default,
            focused ? CellStyle.Bold : CellStyle.Dim);
    }

    private void DrawStatusLine(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        var text = FocusedPane.StatusText;
        if (_panes.Count > 1)
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
        CellText.Draw(grid, rect.X, rect.Y, text, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
    }

    private void DrawDividers(CellGrid grid, UiLayout layout)
    {
        var verticalX = _panes.Count == 1 ? RightPanelDividerColumn(layout) : -1;
        DrawHorizontalDivider(grid, layout.Input.Y - 1, verticalX, layout.Main.Bottom, layout.Input.Y);
        DrawHorizontalDivider(grid, layout.Status.Y - 1, verticalX, layout.Input.Bottom, layout.Status.Y);
        if (verticalX < 0 || layout.Main.Height <= 0)
            return;
        for (var y = layout.Main.Y; y < layout.Main.Bottom && y < grid.Height; y++)
            grid[verticalX, y] = new Cell('│', AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
    }

    private static void DrawHorizontalDivider(CellGrid grid, int y, int verticalX, int minimumY, int maximumY)
    {
        if (y < minimumY || y >= maximumY || y < 0 || y >= grid.Height)
            return;
        for (var x = 0; x < grid.Width; x++)
            grid[x, y] = new Cell(x == verticalX ? '┼' : '─', AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
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
                    grid[divider.X, y] = new Cell('│', AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
            }
        }

        foreach (var divider in horizontals)
        {
            if (divider.Y < 0 || divider.Y >= grid.Height)
                continue;
            for (var x = divider.X; x < divider.X + divider.Length && x < grid.Width; x++)
            {
                if (x >= 0)
                    grid[x, divider.Y] = new Cell('─', AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
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
                    grid[x, y] = new Cell('┼', AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
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
        _ = RefreshProviderCatalogCandidatesAsync();
    }

    private async Task RefreshProviderCatalogCandidatesAsync()
    {
        try
        {
            var snapshot = await ProviderCatalog.LoadAsync(_home, refresh: false);
            _providerCatalog = snapshot;
            _providerCatalogCandidates = CatalogCandidates(snapshot);
            if (snapshot.Error is { Length: > 0 } error)
                _ctx.LoggerFor("tui").Warn($"provider catalog: {error}");
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
        Pane,
        RemotePane,
        Session,
    }

    private sealed record OverviewItem(string Line, OverviewTargetKind Kind, int? PaneId, string? SessionId, string? PtyId = null);
}
