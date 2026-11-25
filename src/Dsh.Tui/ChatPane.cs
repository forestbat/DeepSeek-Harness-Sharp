using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Agent;

namespace Dsh.Tui;

/**
 * 单个 agent 绑定的视图状态: 渲染器、滚动/输入缓冲、fold 选择、菜单与待决交互。
 * ChatWindow 持有 pane 集合与分割树, 键盘只进焦点 pane; 后台 pane 只更新自己的渲染器。
 */
public sealed class ChatPane : ITuiPane
{
    private const string InputPrompt = "> ";

    private readonly ChatWindow _window;
    private readonly Dictionary<ToolCallId, (int Start, string Arguments)> _pendingSubagentCalls = [];
    private AskUserQuestionRequest? _questionRequest;
    private readonly List<AskUserQuestionAnswerItem> _questionAnswers = [];
    private ChoicePromptControl? _choicePrompt;
    private int _questionIndex;
    private string? _stashedInput;
    private int _stashedCursor;
    private IReadOnlyList<string> _mentionCandidates = [];
    /** 会话的上下文占用(最近一次请求提示词 / 模型窗口), 显示在状态行。 */
    private readonly SessionStatistics _stats = new();

    /** 上下文整理是否在跑: 它不属于回合, 需要单独的状态位才能在状态行上显示出来。 */
    private bool _maintaining;

    /** 整理开始时刻(TickCount64): 状态行按它显示"已经压了多久", 压缩慢时用户能看出还在进行。 */
    private long _maintenanceStartedAt;

    /** 由插件提供的中立呈现接缝; 没安装该插件时为 null, 只是不显示整理进度。 */
    private readonly IContextMaintenancePresenter? _maintenance;
    private int _mentionIndex;
    private int _mentionEnd;
    private int _mentionStart;
    private bool _mentionActive;
    private PopupList.Window _mentionWindow;
    private int? _mentionScrollGrab;
    private readonly List<ImageAttachmentRef> _attachments = [];
    /** Ctrl+P 唤起命令选单时暂存的提示词: 选中命令后接到命令后面。 */
    private string _promptText = "";
    private readonly ScrollWheel _wheel = new();

    /** 右栏(单窗格侧栏)自己的滚动状态: 内容行数常超屏, 需要独立于正文的滚动偏移。 */
    private readonly ScrollWheel _rightPanelWheel = new();

    private int _rightPanelScroll;
    private int _rightPanelRows = 1;

    /** 右栏按面板宽折行后的总行数(滚轮夹取用); 每次 DrawRightPanel 按当前宽度重算。 */
    private int _rightPanelLineCount;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    /** 切进历史之前那份草稿(内容 + 光标): 按 ↓ 回到"当前草稿"时原样恢复, 用户写的东西不会因为看了眼历史就丢。 */
    private string _draftBeforeHistory = "";
    private int _draftCursorBeforeHistory;
    private bool _sessionRenamedSubscribed;
    private int _wrapCacheVersion = -1;
    private int _wrapCacheWidth = -1;
    private int _wrapCacheResetVersion = -1;
    /** 折行后的视觉行: 文本按区间从转录正文取, 只有折叠头/页码这类合成行才自带字符串。 */
    private List<WrapRow>? _wrapRows;
    private int _wrapProcessedLine;
    private List<VisualRow> _visualRows = [];
    private readonly List<(int Start, int End, bool Collapsed, int Scroll)> _foldSnapshot = [];
    /** 已被回退事件收掉的日志区间(闭区间): 这些事件还在日志里, 但不再属于会话。 */
    private readonly List<(long From, long To)> _reverted = [];
    /** 界面显示的回合号: 按 turn/start 在日志里出现的先后递增(payload 的 turn 只用于配对, 见 §8.1)。 */
    private int _displayTurn;
    private int _currentDisplayTurn;

    public ChatPane(ChatWindow window, int id, AgentLoopAgent agent)
    {
        _window = window;
        _maintenance = window.MaintenancePresenter;
        // 字段初始器早于本构造函数执行, 那时还拿不到接缝, 因此在这里重建一次。
        Renderer = new TranscriptRenderer(_maintenance);
        StatusText = ReadyStatus;
        Id = id;
        Agent = agent;
        // 打点: 视口=窗格建好(用户看到会话切过去了), 首帧=第一次画出正文, 可交互=回放返回。
        Probe = new TuiOpenProbe(agent.Session.Id.Value);
        Probe.MarkViewport();
        // 转录从会话的常驻窗口开始: 更早的前缀在用户往上滚/跳转时按页补(见 LoadOlderHistory)。
        _replayFrom = agent.Session.ResidentFrom;
        // 用 Reset 而不是 CoverFrom: CoverFrom 是"把覆盖起点往下推", 对初始 0 取 min 等于没设,
        // 于是新窗格会自称"索引已覆盖整段", /timestamp 跳过补扫后候选只剩尾窗里的那几条。
        _timeline.Reset(_replayFrom);
        SubscribeSessionRenamed();
        ReplayEvents();
        Probe.MarkInteractive();
    }

    public int Id { get; }

    public TuiPaneKind Kind => TuiPaneKind.Chat;

    public AgentLoopAgent Agent { get; private set; }

    public Session Session => Agent.Session;

    public string PaneTitle
    {
        get
        {
            var title = Agent.Session.Header.Title;
            return string.IsNullOrWhiteSpace(title) ? Agent.Id.ToString() : $"{Agent.Id}: {title}";
        }
    }

    public TranscriptRenderer Renderer { get; private set; } = new();

    public long RenderedSeq { get; private set; }

    /** 内容快照走渲染器的加锁拷贝, 可在任意线程调用。 */
    public IReadOnlyList<string> SnapshotLines() => Renderer.SnapshotLines();

    /** 命令/mention 浮层画在整帧最后一层(见 ChatWindow.DrawPaneOverlays), 避免被右栏/输入/状态覆盖。 */
    public bool HasOverlay => CommandMenu?.IsActive == true || _mentionActive || Result.IsActive || Timeline.IsActive;

    /** `/timestamp` 时间线浮层: 一级选消息, 二级选动作。 */
    internal TimelineOverlay Timeline { get; } = new();

    /** 动作确认后由 ChatWindow 执行(它持有 fork/revert 的真正实现)。 */
    private Action<string, string>? _timelineExecute;

    /** 打开时间线浮层; 空候选时退化为状态行一句话。 */
    public void OpenTimeline(IReadOnlyList<CommandCandidate> candidates, Action<string, string> execute)
    {
        if (candidates.Count == 0)
        {
            ShowResult("/timestamp 结果", "本会话还没有用户消息", isError: false);
            return;
        }
        _timelineExecute = execute;
        Timeline.Open(candidates);
        if (CommandMenu?.IsActive == true)
        {
            CommandMenu.Close();
            RefreshMenuStatus();
        }
    }

    /** 审批用 y/a/n/c 快捷回答, 其余按键交给提问面板本身(方向键/空格/数字/回车/Esc)。 */
    private void HandleChoicePromptKey(ConsoleKeyInfo key)
    {
        if (_choicePrompt is null)
            return;
        if (PendingApproval is not null && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0)
            return;
        var approvalShortcut = PendingApproval is not null;
        if (approvalShortcut && (key.Key == ConsoleKey.Y || key.KeyChar is 'y' or 'Y'))
            AnswerApproval(ApprovalOutcome.AllowedOnce);
        else if (approvalShortcut && (key.Key == ConsoleKey.A || key.KeyChar is 'a' or 'A'))
            AnswerApproval(ApprovalOutcome.AllowedForSession);
        else if (approvalShortcut && (key.Key == ConsoleKey.N || key.KeyChar is 'n' or 'N'))
            AnswerApproval(ApprovalOutcome.Rejected);
        else if (approvalShortcut && (key.Key == ConsoleKey.C || key.KeyChar is 'c' or 'C'))
            AnswerApproval(ApprovalOutcome.Cancelled);
        else
            _choicePrompt.HandleKey(key);
    }

    private void HandleTimelineKey(ConsoleKeyInfo key)
    {
        if (Timeline.AwaitingConfirm)
        {
            if (key.Key == ConsoleKey.Y || key.KeyChar is 'y' or 'Y')
            {
                // 确认回退: 交给 ChatWindow 走既有的 "--confirm" 执行路径。
                ExecuteTimeline("revert");
                return;
            }
            if (key.Key == ConsoleKey.N || key.KeyChar is 'n' or 'N' || key.Key == ConsoleKey.Escape)
            {
                Timeline.AnswerConfirm(false);
            }
            Renderer.BumpVersion();
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                Timeline.Move(-1);
                break;
            case ConsoleKey.DownArrow:
                Timeline.Move(1);
                break;
            case ConsoleKey.Escape:
                Timeline.Back();
                break;
            case ConsoleKey.Backspace:
                Timeline.BackspaceFilter();
                break;
            case ConsoleKey.Enter:
                if (Timeline.ConfirmSelection() is { } action)
                    ExecuteTimeline(action);
                break;
            default:
                if (key.KeyChar is { } character && !char.IsControl(character))
                    Timeline.AppendFilter(character);
                break;
        }
        Renderer.BumpVersion();
    }

    /** 复制结果浮层全文: 长输出常常是要贴到别处的, 让用户不必手工选择。 */
    public void CopyResultToClipboard()
    {
        if (!Result.IsActive || Result.Text.Length == 0)
            return;
        var text = Result.Text;
        _ = Clipboard.TrySetTextAsync(text).ContinueWith(
            copied => _window.QueueAction(() => StatusText = copied.Result ? "已复制结果到剪贴板" : "复制失败: 剪贴板不可用"),
            TaskScheduler.Default);
    }

    private void ExecuteTimeline(string action)
    {
        var target = Timeline.Selected?.Value;
        var execute = _timelineExecute;
        Timeline.Close();
        _timelineExecute = null;
        if (target is null || execute is null)
            return;
        execute(action, target);
    }


    /** 命令结果浮层: 只读、非模态; 与命令菜单互斥(见 ShowResult)。 */
    internal ResultOverlay Result { get; } = new();

    /**
     * 命令结果落点: 单行且放得下 → 状态行(不弹浮层); 多行 → 结果浮层。
     * 单行结果保持到下一次输入或下一次状态切换(见 SetStatusQuiet)。
     */
    public void ShowResult(string title, string text, bool isError)
    {
        if (!text.Contains('\n', StringComparison.Ordinal))
        {
            _stickyStatus = isError ? $"x {text}" : text;
            SetStatusReady();
            return;
        }
        // 结果浮层优先于命令菜单: 两者同时出现会互相遮挡。
        if (CommandMenu?.IsActive == true)
        {
            CommandMenu.Close();
            RefreshMenuStatus();
        }
        Result.Show(title, text, isError);
    }


    public void DrawOverlay(CellGrid grid, ConsoleRect rect)
    {
        if (Timeline.IsActive)
        {
            DrawTimelineOverlay(grid, rect);
            return;
        }
        // 结果浮层压在命令菜单之上: 先画结果, 用户按 Esc 关掉后才看得到底层菜单。
        if (Result.IsActive)
        {
            Result.VisibleRows = Math.Max(1, PopupList.WindowOf(rect, 0, Result.Lines.Count, Result.First).Visible);
            PopupList.Draw(grid, rect, Result.Title, [], Result.Lines, Result.First, null, scrollbar: true);
            return;
        }
        DrawMenuOverlay(grid, rect);
    }

    public string Input { get; set; } = "";

    public int Cursor { get; set; }

    public int ScrollOffset { get; set; }

    /**
     * 滚到第 index 条用户消息的首行(供 /timestamp jump)。
     * 按"渲染时记下的源偏移"定位, 而不是按文本匹配: 文本匹配会命中更早引用过同一句话的工具输出/消息,
     * 首行被折行折断时还会找不到。
     * index 是全量用户消息里的位置; 转录裁头丢掉更早的消息后, 渲染侧序号要相应前移(total - 渲染数)。
     */
    internal bool TryScrollToUserMessage(int index, int total)
    {
        var adjusted = index - Math.Max(0, total - Renderer.UserMessageCount);
        if (adjusted < 0 || Renderer.UserMessageStartAt(adjusted) is not { } offset)
            return false;
        return TryScrollToSourceOffset(offset);
    }

    /**
     * 视口顶行的实际文本。
     * 补页/重画之后它必须和之前相同 —— 这是"上面多了东西, 我在看的那行没动"的直接判据。
     */
    internal string ViewportTopText()
    {
        var rows = _wrapRows;
        if (rows is null || rows.Count == 0)
            return "";
        return new string(RowSpan(rows[Math.Clamp(ScrollOffset, 0, rows.Count - 1)]));
    }

    /**
     * 把视口顶行放到"源偏移 offset 所在的那一视觉行"。
     * 视口缓存是绘制时按宽度建的: 刚切会话/刚重放过时它是空的, 这里补建一次,
     * 否则"跳不过去"取决于用户有没有先滚一下。
     */
    private bool TryScrollToSourceOffset(int offset)
    {
        EnsureWrapCache();
        var rows = _visualRows;
        if (rows.Count == 0)
            return false;
        // 源偏移 -> 视觉行: 找最后一个 SourceStart <= offset 的视觉行。
        var low = 0;
        var high = rows.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (rows[middle].SourceStart <= offset)
                low = middle + 1;
            else
                high = middle;
        }
        StickToBottom = false;
        ScrollOffset = low == 0 ? 0 : rows[low - 1].FlatStart;
        return true;
    }

    /** 上次绘制正文用的宽度: 跳转要按需重建视口缓存时用它。 */
    private int _lastTranscriptWidth;

    /**
     * 锚点: 视口顶行对应的源偏移, 以及它所属的事件序号。
     *
     * 补页/回退重画/重编号重画都要"整篇重放", 行号会整体位移; 只有按源偏移 + 事件序号还原,
     * 用户正在看的那一行才会留在原处(按字数差加减是错的, 那会把视口甩到底部)。
     */
    private int? _anchorSourceOffset;
    private long _anchorSeq;

    /** 打开/补页打点: 只在会话打开后的观测窗口内计时, 窗口外空闲阻塞不计入 stall。 */
    internal TuiOpenProbe? Probe { get; set; }

    private void EnsureWrapCache()
    {
        if (_wrapRows is { Count: > 0 } || _lastTranscriptWidth <= 0)
            return;
        RebuildWrapCache(_lastTranscriptWidth);
        _wrapCacheVersion = Renderer.Version;
    }

    public bool StickToBottom { get; set; } = true;

    /** 刚刚往下滚过: 下一帧知道 maxOffset 后决定是否恢复贴底(只有用户可以改判跟随)。 */
    private bool _resumeStickAtTail;

    private void RequestStickToBottom() => StickToBottom = true;

    public string? SelectedFoldKey { get; set; }

    private bool _selectionActive;
    private int _selectionAnchorRow;
    private int _selectionAnchorColumn;
    private int _selectionRow;
    private int _selectionColumn;

    public TaskCompletionSource<object?>? PendingApproval { get; private set; }

    public TaskCompletionSource<object?>? PendingQuestions { get; private set; }

    public CommandMenuState? CommandMenu { get; private set; }

    public bool Busy { get; private set; }

    private string _statusText = "";

    /**
     * 状态行文本; 读取时自动拼上上下文占用与"更早的历史为什么接不上", 因此用量/补页状态一更新
     * (无需额外刷新)就会显示出来。补页失败不能静默 —— 否则用户看到的就是"顶也加载不出来、底也加载不出来"。
     */
    public string StatusText
    {
        get
        {
            var status = ContextStatusOf(_statusText);
            var reason = OlderHistoryStatusText;
            if (reason is null)
                return status;
            return status.Length == 0 ? reason : $"{status} · {reason}";
        }
        set => _statusText = value;
    }

    public string? DeleteConfirmSessionId { get; set; }

    public int CursorScreenX { get; private set; }

    public int CursorScreenY { get; private set; }

    public void HandleKey(ConsoleKeyInfo key)
    {
        // 提问/审批面板优先于所有浮层: 面板在等用户回答, 方向键/空格不能被浮层吃掉。
        if (_choicePrompt is not null)
        {
            HandleChoicePromptKey(key);
            return;
        }
        // 时间线浮层是选择器: 打开期间独占按键(含可打印字符, 用来过滤候选)。
        if (Timeline.IsActive)
        {
            HandleTimelineKey(key);
            return;
        }
        // 结果浮层非模态: 只拦截导航键, 其余按键继续往下走, 用户可以一边看结果一边敲提示词。
        if (Result.HandleKey(key))
        {
            Renderer.BumpVersion();
            return;
        }
        if (_choicePrompt is not null)
        {
            HandleChoicePromptKey(key);
            return;
        }

        if (SelectedFoldKey is not null)
        {
            if (key.Key == ConsoleKey.Escape)
            {
                SelectedFoldKey = null;
                return;
            }
            if (key.Key == ConsoleKey.Tab)
            {
                SelectNextFold(1);
                return;
            }
            if (key.Key == ConsoleKey.Enter)
            {
                var selected = FindSelectedFold();
                if (selected is not null)
                {
                    ToggleFold(selected);
                    SelectedFoldKey = null;
                    return;
                }
            }
        }

        if (key.Key == ConsoleKey.Tab && CommandMenu?.IsActive != true && !_mentionActive)
        {
            if (SelectedFoldKey is null)
            {
                var first = Renderer.Folds.FirstOrDefault();
                if (first is not null)
                    SelectedFoldKey = FoldKey(first);
            }
            else
            {
                SelectNextFold(1);
            }
            return;
        }

        if (CommandMenu?.IsActive == true || _mentionActive)
        {
            if (key.Key == ConsoleKey.Delete
                && CommandMenu?.IsActive == true
                && CommandMenu.Stage == CommandMenuState.MenuStage.Argument
                && string.Equals(CommandMenu.CurrentCommand?.Name, "session", StringComparison.OrdinalIgnoreCase))
            {
                if (DeleteConfirmSessionId is null)
                {
                    var candidate = CommandMenu.CandidateValues.ElementAtOrDefault(CommandMenu.SelectedIndex);
                    if (candidate is not null)
                    {
                        DeleteConfirmSessionId = candidate;
                        AppendRaw($"  press Delete again to delete session {candidate}\n");
                    }
                }
                else
                {
                    var id = DeleteConfirmSessionId;
                    DeleteConfirmSessionId = null;
                    _window.RunSlashCommand(this, $"/session delete {id}");
                }
                return;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                DeleteConfirmSessionId = null;
                if (CommandMenu?.IsActive == true)
                {
                    CommandMenu.Back();
                    SyncCommandMenuInput();
                    if (CommandMenu.IsActive != true)
                        RestorePromptInput();
                }
                else
                {
                    CloseMentionMenu();
                }

                RefreshMenuStatus();
                return;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                MoveMenuSelection(-1);
                return;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                MoveMenuSelection(1);
                return;
            }

            if (key.Key == ConsoleKey.Home && CommandMenu?.IsActive == true)
            {
                CommandMenu.MoveHome();
                RefreshMenuStatus();
                return;
            }

            if (key.Key == ConsoleKey.End && CommandMenu?.IsActive == true)
            {
                CommandMenu.MoveEnd();
                RefreshMenuStatus();
                return;
            }

            if (key.Key == ConsoleKey.PageUp && CommandMenu?.IsActive == true)
            {
                CommandMenu.MovePage(-1, MenuPageRows());
                RefreshMenuStatus();
                return;
            }

            if (key.Key == ConsoleKey.PageDown && CommandMenu?.IsActive == true)
            {
                CommandMenu.MovePage(1, MenuPageRows());
                RefreshMenuStatus();
                return;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                if (CommandMenu?.IsActive == true)
                {
                    // 参数阶段: 回车=采纳当前参数(输入/高亮候选/预置)并走到下一条待填参数, 全部满足才回填完整命令;
                    // 命令/子命令阶段: 有候选=选中高亮项; 无候选才是"浮层没得选", 按输入原样发送(未知命令/普通提示词)。
                    if (CommandMenu.Stage == CommandMenuState.MenuStage.Argument
                        || CommandMenu.Candidates.Count > 0)
                    {
                        ConfirmCommandMenu();
                        return;
                    }

                    CommandMenu.Close();
                    RefreshMenuStatus();
                    Submit();
                    return;
                }

                if (_mentionActive && _mentionCandidates.Count > 0)
                {
                    InsertMentionCandidate();
                    return;
                }

                if (_mentionActive)
                    CloseMentionMenu();
            }

            if (key.Key == ConsoleKey.Tab && CommandMenu?.IsActive == true)
            {
                // 参数阶段: Tab 在所有参数之间循环(走到最后一条回到第一条), 不结束命令也不退回上一级; 收尾交给回车。
                if (CommandMenu.Stage == CommandMenuState.MenuStage.Argument)
                {
                    if (!ApplyProviderAddPreset())
                        CommandMenu.MoveToNextArgument();
                    RefreshMenuStatus();
                    return;
                }

                ConfirmCommandMenu();
                return;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.J when (key.Modifiers & ConsoleModifiers.Control) != 0:
                // 有的宿主把 Shift+Enter 与 Enter 送成同一个键(收不到 Shift), Ctrl+J 是可靠的换行替代。
                InsertLineBreak();
                break;
            case ConsoleKey.Enter when (key.Modifiers & ConsoleModifiers.Shift) != 0:
                // Shift+Enter 写多行草稿: 长提示词要能分段, 而不是只能攒成一行。
                InsertLineBreak();
                break;
            case ConsoleKey.Enter:
                Submit();
                break;
            case ConsoleKey.Escape:
                if (Busy)
                    Agent.Cancel(new AgentCancelCause.User());
                break;
            case ConsoleKey.UpArrow when MoveCursorByRow(-1):
                break;
            case ConsoleKey.DownArrow when MoveCursorByRow(1):
                break;
            // 只有在草稿开头按 ↑ 才切历史: 否则用户只是想把光标往上挪, 修改却被整段替换。
            case ConsoleKey.UpArrow when Cursor == 0:
                RecallHistory(-1);
                break;
            // 同理, 只有停在草稿末尾按 ↓ 才切下一条。
            case ConsoleKey.DownArrow when Cursor == Input.Length:
                RecallHistory(1);
                break;
            case ConsoleKey.LeftArrow:
                Cursor = Math.Max(0, Cursor - 1);
                break;
            case ConsoleKey.RightArrow:
                Cursor = Math.Min(Input.Length, Cursor + 1);
                break;
            case ConsoleKey.Home:
                if (Input.Length > 0)
                    Cursor = 0;
                else
                    JumpToTranscriptHead();
                break;
            case ConsoleKey.End:
                if (Input.Length > 0)
                    Cursor = Input.Length;
                else
                    JumpToTranscriptTail();
                break;
            case ConsoleKey.Backspace:
                if (Cursor > 0)
                {
                    Input = Input.Remove(Cursor - 1, 1);
                    Cursor--;
                    RefreshMenus();
                }

                break;
            case ConsoleKey.Delete:
                if (Cursor < Input.Length)
                {
                    Input = Input.Remove(Cursor, 1);
                    RefreshMenus();
                }

                break;
            case ConsoleKey.PageUp:
                StickToBottom = false;
                ScrollOffset = Math.Max(0, ScrollOffset - 10);
                // 滚到顶就自动接上更早的一页(与滚轮同一个入口)。
                if (ScrollOffset == 0)
                    LoadOlderHistory();
                break;
            case ConsoleKey.PageDown:
                ScrollOffset += 10;
                // 与滚轮向下同一判定: 落到末尾就恢复跟随, 否则用 PageDown 到底后新输出会把视口留在旧位置。
                _resumeStickAtTail = true;
                break;
            default:
                if (key.KeyChar >= ' ')
                {
                    Input = Input.Insert(Cursor, key.KeyChar.ToString());
                    Cursor++;
                    RefreshMenus();
                }

                break;
        }
    }

    /**
     * 草稿换行(Shift+Enter / Ctrl+J)。不能复用 InsertText: 那条路径为括号粘贴把 \r\n 压成空格,
     * 换行会被悄悄吃掉, 用户看到的就是"按了 Shift+Enter 没反应"。
     */
    internal void InsertLineBreak()
    {
        if (_choicePrompt is not null)
            return;
        _stickyStatus = null;
        Input = Input.Insert(Cursor, "\n");
        Cursor += 1;
        RefreshMenus();
    }

    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (_choicePrompt is not null)
        {
            _choicePrompt.InsertText(text);
            return;
        }
        var sanitized = text.Replace('\r', ' ').Replace('\n', ' ');
        if (sanitized.Length == 0)
            return;
        _stickyStatus = null;
        Input = Input.Insert(Cursor, sanitized);
        Cursor += sanitized.Length;
        RefreshMenus();
    }

    /** 括号粘贴: 本地图片路径转附件; 空粘贴尝试读 OS 剪贴板图片; 其余按文本插入。 */
    internal void HandlePaste(string text)
    {
        if (_choicePrompt is not null)
        {
            _choicePrompt.InsertText(text);
            return;
        }
        if (text.Length == 0)
        {
            TryAttachClipboardImage();
            return;
        }
        var remaining = AttachImagePaths(text);
        if (remaining.Length == 0)
        {
            StatusText = $"已附加 {_attachments.Count} 张图片 - Enter 发送";
            return;
        }
        InsertPastedText(remaining);
    }

    /**
     * 粘贴文本按原样进草稿: 换行是粘贴内容的一部分, 一条消息就是一条消息。
     * 不能复用 InsertText —— 它为单行插入把 \r\n 压成空格。
     */
    internal void InsertPastedText(string text)
    {
        var normalized = text.ReplaceLineEndings("\n");
        _stickyStatus = null;
        Input = Input.Insert(Cursor, normalized);
        Cursor += normalized.Length;
        RefreshMenus();
    }

    private bool TryAttachClipboardImage()
    {
        try
        {
            if (!ClipboardImage.TryRead(out var bytes, out var mediaType))
                return false;
            var store = Agent.Ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName, false);
            if (store is null)
                return false;
            _attachments.Add(store.Put(bytes, mediaType, 0, 0, "clipboard"));
            StatusText = $"已附加 {_attachments.Count} 张图片 - Enter 发送";
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /** 鼠标滚轮只作用于收到的 pane; 由调用方按命中测试路由。 */
    public void HandleMouseWheel(float delta)
    {
        if (_choicePrompt is not null)
        {
            _choicePrompt.Scroll(delta > 0 ? -3 : delta < 0 ? 3 : 0);
            return;
        }
        var lines = _wheel.Scroll(delta);
        if (lines > 0)
        {
            StickToBottom = false;
            ScrollOffset = Math.Max(0, ScrollOffset - lines);
            // 滚到顶就自动接上更早的一页: 界面上不该存在"更早的没载入"这件事。
            if (ScrollOffset == 0)
                LoadOlderHistory();
        }
        else if (lines < 0)
        {
            // 往下滚到末尾就恢复跟随; 这里拿不到视口高度, 由 DrawTranscript 按真实 maxOffset 判定。
            ScrollOffset += -lines;
            _resumeStickAtTail = true;
        }
    }

    public void HandleInputClick(int cellX, ConsoleRect inputRect)
    {
        Cursor = ColumnToCharIndex(Math.Clamp(cellX - inputRect.X - InputPrompt.Length, 0, TerminalTextWidth.Of(Input)));
        RefreshMenus();
    }

    public void SwitchAgent(AgentLoopAgent agent)
    {
        UnsubscribeSessionRenamed();
        Agent = agent;
        SubscribeSessionRenamed();
        Renderer = new TranscriptRenderer(_maintenance);
        SelectedFoldKey = null;
        RenderedSeq = 0;
        // 切会话/重放时先清整理状态: 历史里可能残留未闭合的开始标记, 那种整理早已随进程退出。
        _maintaining = false;
        ScrollOffset = 0;
        StickToBottom = true;
        _replayFrom = agent.Session.ResidentFrom;
        // 换了会话: 上一条的索引、锚点与补页失败原因都不再适用。
        _timeline.Reset(_replayFrom);
        _anchorSourceOffset = null;
        _anchorSeq = 0;
        _olderHistoryError = null;
        InvalidateWrapCache();
        Probe = new TuiOpenProbe(agent.Session.Id.Value);
        Probe.MarkViewport();
        ReplayEvents(null, keepAnchor: false);
        Probe.MarkInteractive();
    }

    /** 一页历史: 与 GUI 同量级(2 万条), 一次只补一段。 */
    private const int OlderHistoryPageEvents = 20_000;

    /** 转录当前是从哪条事件开始重放的: 0 表示整段历史都在转录里。 */
    private long _replayFrom;

    /** 更早的历史还在日志里(转录还没覆盖到 seq 0)。 */
    public bool HasOlderHistory => _replayFrom > 0;

    /** 转录当前覆盖的起点: 0 表示整段历史都已接上。诊断与验收读它。 */
    internal long ReplayFrom => _replayFrom;

    /**
     * 上次补页失败的原因(对应 GUI 的 HistoryIncomplete)。
     *
     * GUI 的取舍是"打不开比少一段严重, 悄悄少一段又比明确说少了哪一段严重": 这里把原因留在状态行,
     * 而不是让补页失败悄无声息 —— 那正是"顶也加载不出来、底也加载不出来"看起来像卡死的原因。
     */
    private string? _olderHistoryError;

    /** 补页失败原因(没有失败时为 null): 由状态行拼出去。 */
    public string? OlderHistoryStatusText => _olderHistoryError;

    /** 用户消息/回合起点/回退区间的轻量索引: `/timestamp` 与回退边界共用一份。 */
    private readonly TranscriptHistoryIndex _timeline = new();

    /** 已收录的用户消息(seq 递增, 已剔除被回退的): `/timestamp` 的候选来源。 */
    internal IReadOnlyList<SessionTimeline.Entry> TimelineEntries => _timeline.Messages;

    /** 索引是否已覆盖整段历史。 */
    internal bool HistoryIndexComplete => _timeline.CoveredFrom <= 0;

    internal long HistoryCoveredFrom => _timeline.CoveredFrom;

    /** 已知的回退区间(整段扫描时要一起滤掉): 后台扫描线程读取快照。 */
    internal IReadOnlyList<(long From, long To)> KnownRevertedRanges => [.. _reverted];

    /** 该序号所属回合的 TurnStart 序号: 回退/分叉的边界, 与 `/timestamp` 同一份索引。 */
    internal long TurnStartOf(long seq) => _timeline.TurnStartOf(seq);

    /** 把后台扫出来的更早区间合并进索引(必须在 UI 线程调用)。coveredFrom 是那次扫描的起点。 */
    internal void MergeEarlierHistory(
        IReadOnlyList<SessionTimeline.Entry> messages,
        IReadOnlyList<long> turnStarts,
        IReadOnlyList<(long From, long To)> reverted,
        long coveredFrom)
        => _timeline.MergeEarlier(messages, turnStarts, reverted, coveredFrom);

    /**
     * 往上接一页更早的历史: 滚到顶部、或跳转到更早的消息时调用。
     *
     * 转录是只增的行缓冲, 所以"往上接"就是从更早的起点整篇重放; 重放完再把视口挪回原来那段内容,
     * 于是用户看到的只是上面多了东西, 阅读位置不动。前缀事件走会话的分页读(窗口外的按页解压)。
     *
     * 那一页只读一次: 读出来的事件直接喂给重放, 重放不再把同一段再读一遍(T-2)。
     * 读失败时保留已渲染的内容、把原因写进状态行 —— 不允许半截重放留在界面上。
     * 返回是否真的接上了(没有更早的历史、或者那一页读不出来时为 false)。
     */
    public bool LoadOlderHistory() => LoadOlderHistoryTo(Math.Max(0, _replayFrom - OlderHistoryPageEvents));

    /**
     * 一次补到 targetFrom 之前(再按一页对齐), 而不是一页一页往上爬。
     *
     * 只读并渲染**新补进来的这一页**, 再把它的文本整块前置: 每补一页的成本是 O(这一页的事件数)。
     * 旧实现每次 Clear 后重放已载入的整段前缀, 逐页补的总成本是 O(页数²) —— 百万条会话补到顶要几分钟。
     */
    public bool LoadOlderHistoryTo(long targetFrom)
    {
        if (_replayFrom <= 0)
        {
            _olderHistoryError = null;
            return false;
        }
        var from = Math.Max(0, Math.Min(_replayFrom - OlderHistoryPageEvents, targetFrom));
        List<SessionEvent> page;
        try
        {
            page = [.. Agent.Session.SnapshotEvents(from, _replayFrom)];
        }
        catch (SessionPageUnavailableException error)
        {
            _olderHistoryError = $"第 {error.Page} 页读不出来(#{error.Seq}), 更早的历史暂时接不上";
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            _olderHistoryError = $"更早的历史读取失败: {error.Message}";
            return false;
        }
        if (page.Count == 0)
        {
            _olderHistoryError = $"更早的历史不在会话日志里(#{from}-#{_replayFrom - 1} 为空)";
            return false;
        }
        _olderHistoryError = null;
        // 先记下视口顶行(按当前滚动位置算, 不用上一帧的), 之后按它把同一行放回原处。
        var anchor = SourceOffsetAt(ScrollOffset) ?? _anchorSourceOffset;
        var anchorSeq = anchor is { } anchorOffset ? Renderer.SeqAt(anchorOffset) : _anchorSeq;
        PrependOlderPage(page, from, anchor, anchorSeq);
        return true;
    }

    /** 把这一页渲染进一份独立转录, 再整块前置; 页尾未闭合的构造就地收口, 保证接缝干净。 */
    private void PrependOlderPage(List<SessionEvent> page, long from, int? anchor, long anchorSeq)
    {
        var staged = new TranscriptRenderer(_maintenance);
        foreach (var sessionEvent in page)
        {
            // 只生成文本: 统计、忙碌状态、回合编号描述的是"会话现在在哪", 补进来的旧历史不该改它们。
            staged.AppendSessionEvent(sessionEvent, replay: true);
        }
        staged.SealBoundary();
        // 这一页很可能一条可见内容都没有(百万条事件里绝大部分是审计决策): 那也算补页成功,
        // 只是没有文字要前置。
        var delta = Renderer.PrependFrom(staged);
        ShiftHistoryOffsets(delta, staged);
        _replayFrom = from;
        var scanned = TranscriptHistoryIndex.OfEvents(page, from, KnownRevertedRanges);
        _timeline.MergeEarlier(scanned.Messages, scanned.TurnStarts, scanned.Reverted, scanned.CoveredFrom);
        RestoreAnchor(anchor is { } offset ? offset + delta : null, anchorSeq);
    }

    /**
     * 前置一页之后挪动按偏移记账的状态, 并把折行缓存接上。
     *
     * 折行只依赖"行文本 + 宽度", 页与页之间互不影响(PrependFrom 保证接缝落在行边界上),
     * 所以已有行不必重折, 只需连同下标一起后移, 再折新前置的那一页(O(这一页))。
     * 早先是整段作废重折: 11.5MB 的会话每补一页要重折 17.9 万行(约 1.6 秒), 补到顶就是 O(页数²)。
     */
    private void ShiftHistoryOffsets(int delta, TranscriptRenderer page)
    {
        if (_wrapRows is { Count: > 0 } && _wrapCacheWidth > 0 && _wrapProcessedLine > 0)
            PrependWrapCache(page, delta);
        else
        {
            _wrapRows = null;
            _visualRows.Clear();
            _foldSnapshot.Clear();
            _wrapProcessedLine = 0;
        }
        if (_pendingSubagentCalls.Count > 0)
        {
            foreach (var (callId, pending) in _pendingSubagentCalls.ToArray())
                _pendingSubagentCalls[callId] = (pending.Start + delta, pending.Arguments);
        }
        if (_anchorSourceOffset is { } anchorOffset)
            _anchorSourceOffset = anchorOffset + delta;
    }

    /** 折新前置的这一页, 再把它插到已折好的行前面; 已有行只做整体平移, 不重折。 */
    private void PrependWrapCache(TranscriptRenderer page, int delta)
    {
        var outerRows = _wrapRows!;
        var outerVisual = _visualRows;
        _wrapRows = [];
        _visualRows = [];
        WrapInto(page, page.Folds, _wrapCacheWidth, 0, page.LineCount);
        var headRows = _wrapRows;
        var headVisual = _visualRows;
        _wrapRows = outerRows;
        _visualRows = outerVisual;
        ShiftRows(outerRows, outerVisual, delta, headRows.Count);
        outerRows.InsertRange(0, headRows);
        outerVisual.InsertRange(0, headVisual);
        _wrapProcessedLine += page.LineCount;
        // 折叠快照要跟着换成合并后的: 不换的话 RebuildWrapCache 会以为折叠变了, 把整段又折一遍。
        SnapshotFolds(Renderer.Folds);
    }

    /** 已折好的行整体后移: 正文偏移 +delta(前面多了一页), 折行下标 +newRows(前面多了那些行)。 */
    private static void ShiftRows(List<WrapRow> rows, List<VisualRow> visual, int delta, int newRows)
    {
        for (var index = 0; index < rows.Count; index += 1)
        {
            var row = rows[index];
            rows[index] = new WrapRow(row.Start + delta, row.Length, row.Literal);
        }
        for (var index = 0; index < visual.Count; index += 1)
        {
            var row = visual[index];
            visual[index] = new VisualRow(row.SourceStart + delta, row.FlatStart + newRows);
        }
    }

    /** 切换会话后回放历史事件, 让恢复/重进的会话立刻可见; 序列守卫会跳过与滞留事件的重合部分。 */
    public void ReplayEvents() => ReplayEvents(null, keepAnchor: true);

    /**
     * 回放 [_replayFrom, 末尾)。prefix 非空时它就是 [_replayFrom, ResidentFrom) 那一段 ——
     * 补页调用方已经读过一次, 这里不再重复读同一批页。
     */
    private void ReplayEvents(IReadOnlyList<SessionEvent>? prefix, bool keepAnchor)
    {
        var session = Agent.Session;
        var anchor = keepAnchor ? _anchorSourceOffset : null;
        var anchorSeq = _anchorSeq;
        SyncRevertedRanges();
        _timeline.KeepBelow(_replayFrom);
        _displayTurn = 0;
        _currentDisplayTurn = 0;
        // 只回放 _replayFrom 之后的事件: 更早的前缀在用户往上滚/跳转时由 LoadOlderHistory 补上,
        // 而不是在打开会话时把整份历史拉回来。
        var earlier = prefix ?? (session.ResidentFrom > _replayFrom
            ? [.. session.SnapshotEvents(_replayFrom, session.ResidentFrom)]
            : []);
        foreach (var sessionEvent in earlier)
            ProcessSessionEvent(sessionEvent, replay: true);
        foreach (var sessionEvent in session.ResidentEvents())
            ProcessSessionEvent(sessionEvent, replay: true);
        // 还在 inbox 里排队、尚未被回合取用的消息不是事件, 回放不会画出来; 这里按当前排队状态补上,
        // 否则切走会话再切回来, 用户会以为自己那条提示丢了。
        if (Agent is AgentLoopAgent loop)
        {
            foreach (var queued in loop.Inbox.NextTurn.Concat(loop.Inbox.NextStep))
                Renderer.AppendQueuedUserMessage(queued);
        }
        if (keepAnchor)
            RestoreAnchor(anchor, anchorSeq);
    }

    /**
     * 按锚点把视口放回去: 先按事件序号找"那条内容还在不在", 再按源偏移落到行;
     * 内容已经不在(回退把它收掉了)就停在原来的偏移附近, 由绘制阶段夹到合法范围。
     */
    private void RestoreAnchor(int? anchor, long anchorSeq)
    {
        if (anchor is not { } offset)
            return;
        StickToBottom = false;
        if (anchorSeq > 0 && Renderer.CurrentLength > 0 && Renderer.SeqAt(offset) != anchorSeq)
        {
            // 重放之后偏移处已是别的内容: 按序号找它原来那一行的新位置。
            if (SourceOffsetOfSeq(anchorSeq) is { } moved)
                offset = moved;
        }
        // 还没有视口缓存(一次都没画过)时 TryScrollToSourceOffset 返回 false: 交给下一帧按新的行号重建。
        TryScrollToSourceOffset(offset);
    }

    /** 第一条属于该事件(或更晚事件)的视觉行的源偏移: 序号在重放后不再落在同一偏移时的退路。 */
    private int? SourceOffsetOfSeq(long seq)
    {
        EnsureWrapCache();
        foreach (var row in _visualRows)
        {
            if (Renderer.SeqAt(row.SourceStart) >= seq)
                return row.SourceStart;
        }
        return null;
    }

    /** 回退之后整段重画: 先按日志重算被收掉的区间, 再从常驻窗口回放一遍。 */
    private void RedrawAfterRevert(SessionRevertPayload revert)
    {
        RedrawFromLog();
        AppendRaw($"\n  已回退到 #{revert.TargetSeq} 之前\n");
    }

    /** 按日志整段重画: 事件被收掉(回退)或整体换了号(重编号)之后, 界面必须与日志重新对齐。 */
    public void RedrawFromLog()
    {
        Renderer.Clear();
        RenderedSeq = 0;
        _replayFrom = Session.ResidentFrom;
        // 回退/重编号之后日志视图整体变了: 索引里已收录的区间不再可信, 从常驻窗口重新累积。
        _timeline.Reset(Session.ResidentFrom);
        ReplayEvents();
    }

    /** 从日志重算"哪些区间已被回退", 回放前调用: 顺序无关, 也不会因为回放自身再触发一次重画。 */
    private void SyncRevertedRanges()
    {
        _reverted.Clear();
        foreach (var sessionEvent in Session.ResidentEvents())
        {
            if (sessionEvent.Data is SessionRevertPayload revert)
                _reverted.Add((revert.TargetSeq, sessionEvent.Seq));
        }
        // 尾窗里的回退区间也进索引: 索引在补页/整段扫描时靠它把更早的消息一起滤掉。
        foreach (var (from, to) in _reverted)
            _timeline.AddReverted(from, to);
    }

    private bool IsReverted(long seq) => _timeline.Contains(seq);

    public void ProcessSessionEvent(SessionEvent sessionEvent, bool replay = false)
    {
        if (sessionEvent.Seq < RenderedSeq)
            return;
        RenderedSeq = sessionEvent.Seq + 1;
        // 回退是一条只追加的事件: 被它收掉的那一段在日志里仍然存在, 但不再属于会话。
        // 界面上它就表现为"少了一段", 增量删除不划算 —— 整段重画一次(与打开会话时的回放同一条路)。
        if (sessionEvent.Data is SessionRevertPayload revert)
        {
            if (!replay)
                RedrawAfterRevert(revert);
            return;
        }
        if (IsReverted(sessionEvent.Seq))
            return;
        // 回合号按"回合起点在日志里的先后"编: payload 里的 turn 只用于配对, 多宿主驱动时会重复(方案 §8.1)。
        if (sessionEvent.Data is TurnStartPayload)
        {
            _displayTurn += 1;
            _currentDisplayTurn = _displayTurn;
        }
        TrackContextUsage(sessionEvent.Data, _currentDisplayTurn);
        switch (sessionEvent.Data)
        {
            case TurnStartPayload:
                SetBusy(true);
                break;
            case TurnEndPayload:
                SetBusy(false);
                break;
        }

        // 上下文整理可能长达数分钟, 期间必须让状态行显示出来; 否则用户只会看到界面没反应,
        // 然后再触发一次 —— 那次必然撞上"已在进行"。只认实时事件: 重放里残留的未闭合标记
        // 属于早已退出的进程, 不能把界面卡在"进行中"。
        if (!replay && _maintenance?.Describe(sessionEvent) is { } maintenance)
            TrackMaintenance(maintenance);

        if (sessionEvent.Data is ToolCallPayload call)
            _pendingSubagentCalls[call.CallId] = (Renderer.CurrentLength, call.Arguments);

        // 时间线索引与回退边界在回放时顺手累积: /timestamp 与 /timestamp revert 之后都只读内存。
        // 过滤规则复用 SessionTimeline.UserText, 不在这里另写一套"什么算用户消息"。
        if (sessionEvent.Data is TurnStartPayload)
            _timeline.AddTurnStart(sessionEvent.Seq);
        var userText = SessionTimeline.UserText(sessionEvent);
        if (userText.Length > 0)
            _timeline.AddMessage(sessionEvent.Seq, sessionEvent.Time, userText);

        if (sessionEvent.Data is ToolResultPayload result)
        {
            var pending = _pendingSubagentCalls.Remove(result.Message.Block.ToolCallId, out var tracked)
                ? tracked
                : (Start: -1, Arguments: "");
            if (pending.Start >= 0 && TryMatchSubagent(pending.Arguments, out var child))
            {
                Renderer.AppendSessionEvent(sessionEvent, replay, foldToolResult: false);
                AppendSubagentSummary(child!, pending.Start);
                return;
            }
        }

        Renderer.AppendSessionEvent(sessionEvent, replay);
    }

    /** 工具调用参数里的 description 与某个子会话的持久化 Label 一致时, 判定该调用为子代理委派。 */
    private bool TryMatchSubagent(string arguments, out SubagentNode? child)
    {
        child = null;
        if (arguments.Length == 0)
            return false;
        string? description = null;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("description", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                description = value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrEmpty(description))
            return false;
        child = _window.Subagents.FindChildByLabel(Agent.Id, description);
        return child is not null;
    }

    /** 上下文占用: 请求上下文给容量分母, 流式/最终用量给分子(只认最近一次请求的提示词)。 */
    private void TrackContextUsage(SessionEventPayload payload, int displayTurn)
    {
        switch (payload)
        {
            case RequestContextPayload context:
                // 上下文窗口到达 = 一次请求开始: 从这里起算首 token 与 LLM 用时。
                _stats.ObserveRequestContext(context.ContextWindow);
                _stats.RequestStarted();
                break;
            case AssistantChunkPayload { Chunk: StreamChunk.Usage usage }:
                _stats.ObserveUsage(usage.Value);
                break;
            case AssistantChunkPayload chunk:
                _stats.ObserveTurn(displayTurn, chunk.Step);
                _stats.FirstTokenArrived();
                break;
            case AssistantMessagePayload { Usage: { } messageUsage } message:
                _stats.ObserveTurn(displayTurn, message.Step);
                _stats.ObserveUsage(messageUsage);
                _stats.RequestFinished();
                break;
            case ToolCallPayload call:
                _stats.ObserveTurn(displayTurn, call.Step);
                break;
        }
    }

    /** 把子会话自身的工具调用/结果摘要追加进 fold 区间, 展开委派调用即可内联查看。 */
    private void AppendSubagentSummary(SubagentNode child, int foldStart)
    {
        var label = child.Label ?? child.Id.Value;
        var session = _window.Subagents.FindSession(child.Id);
        var summary = session is null ? "" : BuildToolCallSummary(session);
        Renderer.AppendRaw(summary.Length == 0
            ? $"\n  subagent {label}: no tool calls\n"
            : $"\n  subagent {label} tool calls:\n{summary}\n");
        Renderer.AddFold(foldStart, Renderer.CurrentLength, "subagent", $"⚙ subagent: {label}");
    }

    private static string BuildToolCallSummary(Session session)
    {
        var renderer = new TranscriptRenderer();
        foreach (var sessionEvent in session.OwnEvents())
        {
            if (sessionEvent.Data is ToolCallPayload or ToolResultPayload)
                renderer.AppendSessionEvent(sessionEvent);
        }

        var lines = new List<string>(renderer.LineCount + 1);
        for (var index = 0; index < renderer.LineCount; index += 1)
            lines.Add($"  {new string(renderer.LineSpan(index))}");
        if (renderer.TailSpan.Length > 0)
            lines.Add($"  {renderer.Tail}");
        return string.Join('\n', lines);
    }

    /**
     * 系统/工具提示文本: 只追加内容, 不改判跟随意图。
     * 跟随只由用户自己的滚动(滚轮/PageUp/Home/End)改变; 否则用户在读历史时, 新来的输出会把视口拽回底部。
     */
    public void AppendRaw(string text)
        => Renderer.AppendSystemMessage(text);

    public void DrawTranscript(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var transcriptVersion = Renderer.Version;
        _lastTranscriptWidth = rect.Width;
        if (Renderer.ResetVersion != _wrapCacheResetVersion)
        {
            // 转录被整体清空重放过: 行号已经重排, 按行号缓存的包裹行与折叠快照都不再对得上, 丢弃重算。
            _wrapCacheResetVersion = Renderer.ResetVersion;
            _wrapRows = null;
            _visualRows.Clear();
            _foldSnapshot.Clear();
        }
        if (_wrapRows is null || _wrapCacheVersion != transcriptVersion || _wrapCacheWidth != rect.Width)
        {
            RebuildWrapCache(rect.Width);
            _wrapCacheVersion = transcriptVersion;
        }
        var wrapped = _wrapRows!;
        if (wrapped.Count == 0)
            wrapped.Add(new WrapRow(0, 0, ""));

        var maxOffset = Math.Max(0, wrapped.Count - rect.Height);
        if (StickToBottom)
            ScrollOffset = maxOffset;
        else if (_resumeStickAtTail)
        {
            // 用户往下滚到的位置就是末尾: 恢复跟随, 让后续输出重新贴底。
            StickToBottom = ScrollOffset >= maxOffset;
        }
        _resumeStickAtTail = false;
        ScrollOffset = Math.Clamp(ScrollOffset, 0, maxOffset);

        var start = ScrollOffset;
        _anchorSourceOffset = SourceOffsetAt(start);
        if (_anchorSourceOffset is { } anchorOffset)
            _anchorSeq = Renderer.SeqAt(anchorOffset);
        Probe?.MarkViewport();
        for (var row = 0; row < rect.Height; row++)
        {
            var lineIndex = start + row;
            if (lineIndex >= wrapped.Count)
                break;
            CellText.Draw(grid, rect.X, rect.Y + row, RowSpan(wrapped[lineIndex]), literal: true);
            ApplyTintHighlight(grid, rect, row, lineIndex);
            ApplySelectionHighlight(grid, rect, row, lineIndex);
        }
        Probe?.MarkFirstPaint();
    }

    /** 时间线浮层: 一级列消息(含 IDE 历史项), 二级列动作; revert 的确认走同一浮层的提示行。 */
    private void DrawTimelineOverlay(CellGrid grid, ConsoleRect rect)
    {
        if (Timeline.PickingAction)
        {
            var headers = new List<string> { Timeline.Selected?.Label ?? "" };
            if (Timeline.AwaitingConfirm)
            {
                headers.Add("确认回退? y=执行(截断会话并回滚文件) n=取消");
                PopupList.Draw(grid, rect, "revert 确认", headers, [], 0);
                return;
            }
            PopupList.Draw(grid, rect, Timeline.Title, headers, Timeline.ActionLabels, Timeline.ActionIndex);
            return;
        }

        var panel = new List<string>();
        if (Timeline.Filter.Length > 0)
            panel.Add($"> 过滤: {Timeline.Filter}");
        var labels = Timeline.Candidates.Select(candidate => candidate.Label).ToList();
        var descriptions = Timeline.Candidates.Select(candidate => candidate.Description ?? "").ToList();
        PopupList.Draw(grid, rect, Timeline.Title, panel, labels, Timeline.Index, descriptions, scrollbar: true);
    }

    private void DrawMenuOverlay(CellGrid grid, ConsoleRect rect)
    {
        if (CommandMenu?.IsActive == true)
        {
            // 参数阶段画"说明 + 候选"面板; 命令/子命令阶段只在有候选时画列表(无候选不画空浮层)。
            // 有说明行时也要把候选的解说词传进去(如 /session 候选右侧的"带时区时间戳")。
            var headers = CommandMenuPanelLines();
            if (headers.Count > 0)
                PopupList.Draw(grid, rect, CommandMenuTitle(), headers, CommandMenu.Candidates, CommandMenu.SelectedIndex, CommandMenu.CandidateDescriptions);
            else if (CommandMenu.Candidates.Count > 0)
                PopupList.Draw(grid, rect, CommandMenuTitle(), [], CommandMenu.Candidates, CommandMenu.SelectedIndex, CommandMenu.CandidateDescriptions);
            return;
        }

        if (_mentionActive)
        {
            _mentionWindow = PopupList.WindowOf(rect, 0, _mentionCandidates.Count, _mentionIndex);
            PopupList.Draw(grid, rect, $"@ {_mentionCandidates.Count} candidates", [], _mentionCandidates, _mentionIndex, null, scrollbar: true);
        }
    }

    /** 参数面板说明行: > 当前参数(含已输入值) - + 已填 - - 待填; 每行给出 flag/名称、含义与必填。 */
    private IReadOnlyList<string> CommandMenuPanelLines()
    {
        if (CommandMenu?.IsActive != true || CommandMenu.Stage != CommandMenuState.MenuStage.Argument)
            return [];

        var schemas = CommandMenu.ArgumentSchemas;
        if (schemas.Count == 0)
            return [];

        var values = CommandMenu.ArgumentValues;
        var lines = new List<string>(schemas.Count);
        for (var index = 0; index < schemas.Count; index++)
        {
            var schema = schemas[index];
            var prefill = CommandMenu.PrefilledValue(index);
            var typed = index == CommandMenu.ArgumentIndex ? CommandMenu.Query : null;
            var value = typed is { Length: > 0 } ? typed
                : index < values.Count && values[index].Length > 0 ? values[index]
                : prefill ?? "";
            // 标记: > 当前参数 - + 已有值(手输/已采纳/预置) - - 还没值。
            var marker = index == CommandMenu.ArgumentIndex
                ? ">"
                : value.Length > 0 ? "+" : "-";
            var filled = value.Length > 0 ? $" = {value}" : "";
            var autoFilled = prefill is { Length: > 0 }
                && typed is not { Length: > 0 }
                && string.Equals(value, prefill, StringComparison.Ordinal);
            var tag = schema.Required && value.Length == 0
                ? " (必填)"
                : autoFilled ? " (自动填好)" : "";
            lines.Add($"{marker} {ArgumentLabel(schema)}{filled}  {schema.Hint}{tag}");
        }

        return lines;
    }

    private static string ArgumentLabel(CommandArgumentSchema schema)
        => schema.Flag is { Length: > 0 } flag ? flag : schema.Name;

    /** 浮层标题带筛选与位置计数, 便于在数百个候选(如模型)里定位; 参数面板标题用命令全路径。 */
    private string CommandMenuTitle()
    {
        var count = CommandMenu!.Candidates.Count;
        var argumentStage = CommandMenu.Stage == CommandMenuState.MenuStage.Argument;
        var label = argumentStage
            ? $"/{CommandMenu.CurrentCommand?.Name}{(CommandMenu.CurrentSubcommand is { } sub ? $" {sub.Name}" : "")}"
            : CommandMenu.Prompt;
        if (count == 0)
        {
            if (argumentStage)
                return label;
            return CommandMenu.Query.Length > 0
                ? $"{label} - 无匹配 \"{CommandMenu.Query}\""
                : $"{label} - 无候选项";
        }

        var position = $"{CommandMenu.SelectedIndex + 1}/{count}";
        return CommandMenu.Query.Length > 0
            ? $"{label} - {position} 匹配 \"{CommandMenu.Query}\""
            : $"{label} - {position}";
    }

    /** 浮层翻页步长: 参数面板要扣掉说明行。 */
    private int MenuPageRows()
        => PopupList.PageRowsFor(CommandMenu?.Stage == CommandMenuState.MenuStage.Argument
            ? CommandMenu.ArgumentSchemas.Count
            : 0);

    public void DrawRightPanel(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        // 先把每行折到面板宽: 长 session id / cwd 不折行会一路顶到窗口边缘(甚至越过边栏), 后面的内容看不到。
        var lines = WrapRightPanelLines(RightPanelLines(), rect.Width);
        _rightPanelLineCount = lines.Count;
        _rightPanelRows = rect.Height;
        // 内容超屏时按滚动偏移切窗口, 让窄终端也能看到下面的段落(滚轮在 ChatWindow 里路由过来)。
        _rightPanelScroll = Math.Clamp(_rightPanelScroll, 0, Math.Max(0, lines.Count - rect.Height));
        for (var index = 0; index < rect.Height && _rightPanelScroll + index < lines.Count; index++)
        {
            var line = lines[_rightPanelScroll + index];
            var y = rect.Y + index;
            if (line.Separator)
            {
                for (var x = rect.X; x < rect.Right && x < grid.Width; x++)
                    grid[x, y] = new Cell('─', TuiTheme.Divider, AnsiColor.Default, CellStyle.Dim);
                continue;
            }

            CellText.Draw(grid, rect.X, y, line.Text,
                line.Header ? TuiTheme.Highlight : CellColor.Default,
                AnsiColor.Default,
                line.Header ? CellStyle.Bold : CellStyle.Dim);
        }
    }

    /** 右栏按面板宽折行; 分隔线保持单行(它是整宽的横线, 不参与折行)。 */
    private static List<RightPanelLine> WrapRightPanelLines(IReadOnlyList<RightPanelLine> lines, int width)
    {
        var wrapped = new List<RightPanelLine>(lines.Count);
        var buffer = new List<string>();
        foreach (var line in lines)
        {
            if (line.Separator)
            {
                wrapped.Add(line);
                continue;
            }

            buffer.Clear();
            WrapSingleLine(line.Text, width, buffer);
            wrapped.AddRange(buffer.Select(text => line with { Text = text }));
        }

        return wrapped;
    }

    /** 右栏滚动偏移(自顶部的行数), 供测试断言。 */
    internal int RightPanelScrollOffset => _rightPanelScroll;

    /** 右栏滚轮: 返回是否真的滚动了(供调用方决定要不要重绘)。 */
    public bool ScrollRightPanel(float delta)
    {
        var lines = _rightPanelWheel.Scroll(delta);
        if (lines == 0)
            return false;
        var maximum = Math.Max(0, _rightPanelLineCount - _rightPanelRows);
        var next = Math.Clamp(_rightPanelScroll - lines, 0, maximum);
        if (next == _rightPanelScroll)
            return false;
        _rightPanelScroll = next;
        return true;
    }

    private List<RightPanelLine> RightPanelLines()
    {
        var lines = new List<RightPanelLine>();
        var keys = _window.KeyBindings;
        var (provider, model) = CurrentModel(Agent);
        AddPanelSection(lines, "上下文",
        [
            $"session: {Agent.Id}",
            $"title: {Agent.Session.Header.Title ?? "-"}",
            $"cwd: {Agent.Session.WorkingDirectory ?? "-"}",
            $"model: {provider}/{model}",
        ]);
        AddPanelSection(lines, "MCP", _window.McpLines());
        AddPanelSection(lines, "Git 变更", _window.GitLines());
        AddPanelSection(lines, "快捷键",
        [
            "Enter 发送 - / 命令 - @ 引用",
            "Tab 下一个参数/折叠 - Esc 取消",
            "Enter 发送 - Shift+Enter 换行",
            "^/v 历史 - PgUp/PgDn 滚动",
            $"{keys.Keys(TuiAction.Interrupt)}×2 退出",
            keys.Line(new("新会话", TuiAction.NewSession), new("会话", TuiAction.SessionMenu)),
            keys.Line(new("detach", TuiAction.Detach), new("删会话", TuiAction.DeleteSessionMenu)),
            keys.Line(new("总览", TuiAction.Overview), new("分屏", TuiAction.SplitPane), new("关闭", TuiAction.ClosePane)),
            keys.Line(new KeyHint("切窗格", ChatWindow.FocusActions)),
        ]);
        return lines;
    }

    private static void AddPanelSection(List<RightPanelLine> lines, string header, IReadOnlyList<string> content)
    {
        if (lines.Count > 0)
            lines.Add(new RightPanelLine("", Separator: true, Header: false));
        lines.Add(new RightPanelLine(header, Separator: false, Header: true));
        lines.AddRange(content.Select(text => new RightPanelLine(text, Separator: false, Header: false)));
    }

    private sealed record RightPanelLine(string Text, bool Separator, bool Header);

    /**
     * 输入区需要的高度(含说明行): 内容按当前宽度软换行后的行数。
     * 布局在自动模式(用户没拖过高度)下用它, 长提示词因此能整体可见。
     */
    public int RequiredInputRows(int width)
    {
        // 面板自带标题/选项/脚注, 给足行数; 上限由布局按屏幕高度夹取。
        if (_choicePrompt is not null)
            return 14;
        var prompt = PendingApproval is not null ? "approval (y/n/c) " : InputPrompt;
        var rows = InputLayout.Wrap(Input, Math.Max(1, width - prompt.Length)).Count;
        return rows + 1;
    }

    public void DrawInput(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        if (_choicePrompt is not null)
        {
            DrawChoicePrompt(grid, rect);
            return;
        }

        var prompt = PendingApproval is not null ? "approval (y/n/c) " : InputPrompt;
        var availableWidth = Math.Max(1, rect.Width - prompt.Length);
        _lastInputWidth = availableWidth;
        var rows = InputLayout.Wrap(Input, availableWidth);
        var caret = InputLayout.Cursor(Input, Cursor, availableWidth);
        // 最后一行留给说明行: 内容区从 rect.Y 到 rect.Bottom - 1。
        var contentRows = Math.Max(1, rect.Height - 1);
        var first = Math.Clamp(caret.Row - contentRows + 1, 0, Math.Max(0, rows.Count - contentRows));

        for (var index = 0; index < contentRows; index++)
        {
            var rowIndex = first + index;
            if (rowIndex >= rows.Count)
                break;
            var row = rows[rowIndex];
            var y = rect.Y + index;
            if (index == 0)
                CellText.Draw(grid, rect.X, y, prompt, AnsiColor.Default, AnsiColor.Default, PendingApproval is null ? CellStyle.None : CellStyle.Bold);
            var visible = Input.AsSpan(row.Start, row.Length);
            // 行首第一行缩进到提示符之后, 续行与正文对齐, 读起来是一整段。
            CellText.Draw(grid, rect.X + prompt.Length, y, visible, literal: true);
        }

        var cursorX = Math.Clamp(rect.X + prompt.Length + caret.Column, 0, Math.Max(0, grid.Width - 1));
        var cursorY = Math.Clamp(rect.Y + (caret.Row - first), 0, Math.Max(0, grid.Height - 1));
        var cursorCell = grid[cursorX, cursorY];
        grid[cursorX, cursorY] = cursorCell with { Style = cursorCell.Style | CellStyle.Reverse };
        CursorScreenX = cursorX;
        CursorScreenY = cursorY;

        if (rect.Height < 2)
            return;
        var infoY = rect.Bottom - 1;
        if (infoY >= grid.Height)
            return;
        var hint = $"Enter 发送 - / 命令 - @ 引用 - Tab 下一个参数 - {_window.KeyBindings.Leader(TuiAction.SessionMenu)} 会话";
        CellText.Draw(grid, rect.X, infoY, hint, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
        var (currentProvider, currentModel) = CurrentModel(Agent);
        var profile = CurrentReasoningEffort(Agent) is { } effort
            ? $"{currentProvider} - {currentModel} - {effort.Value}"
            : $"{currentProvider} - {currentModel}";
        var profileWidth = TerminalTextWidth.Of(profile);
        if (TerminalTextWidth.Of(hint) + profileWidth + 2 < rect.Width)
            CellText.Draw(grid, rect.Right - profileWidth, infoY, profile, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
    }

    public bool HasChoicePrompt => _choicePrompt is not null;

    /**
     * 提问面板画在输入区内, 不再铺满整屏: 用户回答时要能看见上面的消息,
     * 否则提问一出现历史就被清空, 判断"该选哪一项"没有上下文。
     */
    public void DrawChoicePrompt(CellGrid grid, ConsoleRect rect)
    {
        if (_choicePrompt is not null)
            _choicePrompt.Draw(grid, rect);
    }

    public bool HandleChoicePointer(int x, int y, bool click)
        => _choicePrompt?.HandlePointer(x, y, click) ?? false;

    public bool SynchronizeChoicePrompt()
    {
        if (_choicePrompt is null || PendingApproval?.Task.IsCompleted != true && PendingQuestions?.Task.IsCompleted != true)
            return false;
        PendingApproval = null;
        PendingQuestions = null;
        _questionRequest = null;
        _questionAnswers.Clear();
        CloseChoicePrompt();
        return true;
    }

    private void StashDraft()
    {
        _window.CancelPointerGestures();
        if (_stashedInput is not null)
            return;
        _stashedInput = Input;
        _stashedCursor = Cursor;
        Input = "";
        Cursor = 0;
        CloseMentionMenu();
        CommandMenu = null;
    }

    private void CloseChoicePrompt()
    {
        _choicePrompt = null;
        if (_stashedInput is not null)
        {
            Input = _stashedInput;
            Cursor = _stashedCursor;
            _stashedInput = null;
        }
        SetStatusReady();
    }

    public void ShowApprovalPrompt(ApprovalRequest request, TaskCompletionSource<object?> answer)
    {
        if (PendingApproval is not null)
            AnswerApproval(ApprovalOutcome.Cancelled);
        if (PendingQuestions is not null)
            CompleteQuestions(AskUserQuestionAnswer.Dismissed);
        StashDraft();
        PendingApproval = answer;
        _choicePrompt = new ChoicePromptControl(ChoicePrompt.ForApproval(request.ToolName, request.Arguments, request.Reason));
        _choicePrompt.Completed += dismissed =>
        {
            var value = _choicePrompt?.State.SelectedValues().FirstOrDefault();
            AnswerApproval(dismissed ? ApprovalOutcome.Cancelled : value switch
            {
                "once" => ApprovalOutcome.AllowedOnce,
                "session" => ApprovalOutcome.AllowedForSession,
                "reject" => ApprovalOutcome.Rejected,
                _ => ApprovalOutcome.Cancelled,
            });
        };
        StatusText = $"approval pending for \"{request.ToolName}\" - y/a/n/c";
    }

    private void AnswerApproval(ApprovalOutcome outcome)
    {
        var pending = PendingApproval;
        if (pending is null)
            return;
        PendingApproval = null;
        CloseChoicePrompt();
        pending.TrySetResult(outcome);
    }

    public void ShowQuestionPrompt(AskUserQuestionRequest request, TaskCompletionSource<object?> answer)
    {
        if (PendingApproval is not null)
            AnswerApproval(ApprovalOutcome.Cancelled);
        if (PendingQuestions is not null)
            CompleteQuestions(AskUserQuestionAnswer.Dismissed);
        StashDraft();
        PendingQuestions = answer;
        _questionRequest = request;
        _questionIndex = 0;
        _questionAnswers.Clear();

        ShowCurrentQuestion();
    }

    private void ShowCurrentQuestion()
    {
        var question = _questionRequest!.Questions[_questionIndex];
        _choicePrompt = new ChoicePromptControl(ChoicePrompt.FromQuestion(question));
        _choicePrompt.Completed += dismissed =>
        {
            if (dismissed)
            {
                CompleteQuestions(AskUserQuestionAnswer.Dismissed);
                return;
            }
            _questionAnswers.Add(_choicePrompt!.State.BuildAnswer(question.Id));
            _questionIndex++;
            if (_questionIndex < _questionRequest!.Questions.Count)
                ShowCurrentQuestion();
            else
                CompleteQuestions(new AskUserQuestionAnswer([.. _questionAnswers]));
        };
        StatusText = $"question {_questionIndex + 1}/{_questionRequest.Questions.Count} - Tab custom - Enter submit - Esc dismiss all";
    }

    private void CompleteQuestions(AskUserQuestionAnswer result)
    {
        var pending = PendingQuestions;
        PendingQuestions = null;
        _questionRequest = null;
        _questionAnswers.Clear();
        CloseChoicePrompt();
        pending?.TrySetResult(result);
    }

    internal void RefreshMenus()
    {
        var text = Input;
        if (text.StartsWith('/') && !IsImagePath(text))
        {
            if (CommandMenu is not { IsActive: true })
                CommandMenu = CreateCommandMenu();
            CommandMenu.ApplyInput(text);
            CloseMentionMenu();
        }
        else
        {
            if (CommandMenu?.IsActive == true)
                CommandMenu.Close();
            // 回到普通输入(浮层关闭)时丢弃 Ctrl+P 暂存的提示词, 避免误接到别的命令后面。
            _promptText = "";
            DeleteConfirmSessionId = null;
            if (MentionResolver.TryGetActiveToken(text, Cursor, out var start, out var end))
            {
                _mentionActive = true;
                _mentionStart = start;
                _mentionEnd = end;
                _mentionCandidates = MentionResolver.ResolveCandidates(text[(start + 1)..end], CurrentCwd(), _window.CurrentSessions());
                _mentionIndex = Math.Clamp(_mentionIndex, 0, Math.Max(0, _mentionCandidates.Count - 1));
            }
            else
            {
                CloseMentionMenu();
            }
        }

        RefreshMenuStatus();
    }

    private CommandMenuState CreateCommandMenu()
    {
        var commands = _window.Ctx.Get<CommandsService>(CommandsService.ServiceName)?.List(Agent) ?? [];
        foreach (var local in _window.LocalCommandDescriptors)
        {
            if (commands.All(command => !string.Equals(command.Name, local.Name, StringComparison.OrdinalIgnoreCase)))
                commands = [.. commands, local];
        }
        var descriptors = CommandMenuCatalog.Enrich(commands);
        return new CommandMenuState(descriptors, CommandCandidates);
    }

    private IReadOnlyList<CommandCandidate> CommandCandidates(
        CommandDescriptor command,
        CommandDescriptor? subcommand,
        CommandArgumentSchema? schema)
    {
        try
        {
            // `/provider add` 的 name 候选: 先给 models.dev 目录(选中即带出 baseUrl/type/models), 再给协议族/自定义条目。
            if (string.Equals(command.Name, "provider", StringComparison.OrdinalIgnoreCase)
                && string.Equals(subcommand?.Name, "add", StringComparison.OrdinalIgnoreCase)
                && string.Equals(schema?.Name, "name", StringComparison.OrdinalIgnoreCase))
                return Candidates([.. _window.ProviderCatalogCandidates, .. ProviderTypes.All]);

            // 有子命令时按子命令名给候选(如 provider remove), 否则按命令名。
            var descriptor = subcommand ?? command;
            var settings = HarnessSettings.Load(_window.Home);
            return schema?.Name switch
            {
                // provider 的 --type 候选按已注册适配器动态取(插件启用/停用后依然正确)。
                "type" => Candidates([.. KnownProviderTypes()]),
                "action" when string.Equals(descriptor.Name, "timestamp", StringComparison.OrdinalIgnoreCase) => Candidates("jump", "revert", "fork"),
                "seq" when string.Equals(descriptor.Name, "timestamp", StringComparison.OrdinalIgnoreCase) => _window.TimestampCandidates(this),
                PluginCommand.PackageArgument => Candidates([.. _window.Ctx.Get<HarnessPluginManager>("pluginManager", false)?.PackageNames ?? []]),
                _ => descriptor.Name switch
                {
                    "model" => Candidates([.. settings.Providers
                        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                        .SelectMany(entry => ProviderCatalog
                            .EffectiveModels(
                                _window.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false),
                                entry.Key,
                                entry.Value)
                            .OrderBy(model => model, StringComparer.Ordinal)
                            .Select(model => $"{entry.Key}/{model}"))]),
                    "remove" => Candidates([.. settings.Providers.Keys.OrderBy(name => name, StringComparer.Ordinal)]),
                    // 显示"会话标题 + 带时区的时间戳", 但写入的是会话 id(标题可能为空或重复, 不能当值用)。
                    "session" => _window.SessionCandidates(),
                    "skill" => Candidates(_window.SkillCandidates),
                    "reasoning" => Candidates(ReasoningEffortCandidates()),
                    "gpu" => Candidates(GpuCatalog.SelectionLabels()),
                    _ => [],
                },
            };
        }
        catch (Exception)
        {
            return [];
        }
    }

    /** 参数候选的默认包装: 值即标签(没有单独显示文本的场合)。 */
    private static IReadOnlyList<CommandCandidate> Candidates(params IReadOnlyList<string> values)
        => [.. values.Select(value => new CommandCandidate(value, value))];

    private IReadOnlyList<string> KnownProviderTypes()
    {
        var factories = _window.Ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName, false);
        return factories is null ? [] : ProviderRegistrar.KnownTypeList(factories);
    }

    private IReadOnlyList<string> ReasoningEffortCandidates()
    {
        var (provider, model) = CurrentModel(Agent);
        if (provider is null || model is null)
            return [];
        var info = _window.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false)?.ResolveModelInfo(provider, model);
        return info?.Reasoning?.Efforts.Select(effort => effort.Id.Value).ToList() ?? [];
    }

    private static (string? Provider, string? Model) CurrentModel(IAgent agent)
    {
        var config = agent.Session.RequestHeader()?.Config;
        return (config?.Provider ?? agent.Options.Provider, config?.Model ?? agent.Options.Model);
    }

    private static ReasoningEffortId? CurrentReasoningEffort(IAgent agent)
        => agent.Session.RequestHeader()?.Config.ReasoningEffort ?? agent.Options.ReasoningEffort;


    /** 输入行为空时 Home/End 直达记录头/尾(有内容时留给输入光标)。 */
    private void JumpToTranscriptHead()
    {
        StickToBottom = false;
        ScrollOffset = 0;
    }

    private void JumpToTranscriptTail()
    {
        ScrollOffset = 0;
        StickToBottom = true;
    }

    private void MoveMenuSelection(int direction)
    {
        if (CommandMenu?.IsActive == true)
        {
            if (direction < 0)
                CommandMenu.MoveUp();
            else
                CommandMenu.MoveDown();
        }
        else if (_mentionActive && _mentionCandidates.Count > 0)
        {
            _mentionIndex = Math.Clamp(_mentionIndex + direction, 0, _mentionCandidates.Count - 1);
        }

        RefreshMenuStatus();
    }

    /** 滚轮滚动命令/mention 浮层候选(与 ^/v 等价); 浮层无候选返回 false 以便正文继续滚动。delta>0=向上(与正文滚动同约定)。 */
    internal bool ScrollOverlay(float delta)
    {
        if (delta == 0)
            return false;
        if (CommandMenu?.IsActive == true && CommandMenu.Candidates.Count > 0)
        {
            MoveMenuSelection(delta > 0 ? -1 : 1);
            return true;
        }
        if (_mentionActive && _mentionCandidates.Count > 0)
        {
            _mentionIndex = Math.Clamp(_mentionIndex + (delta > 0 ? -1 : 1), 0, _mentionCandidates.Count - 1);
            RefreshMenuStatus();
            return true;
        }
        return false;
    }

    /** 命中 mention 浮层滚动条则开始拖动: 返回是否接管该次按下。 */
    internal bool TryBeginMentionScrollDrag(int cellX, int cellY)
    {
        if (!_mentionActive)
            return false;
        var bar = PopupList.ScrollbarOf(_mentionWindow, _mentionCandidates.Count);
        if (bar is null || cellX != bar.Column || cellY < bar.TrackTop || cellY >= bar.TrackTop + bar.TrackHeight)
            return false;
        var onThumb = cellY >= bar.TrackTop + bar.ThumbTop && cellY < bar.TrackTop + bar.ThumbTop + bar.ThumbHeight;
        _mentionScrollGrab = onThumb ? cellY - (bar.TrackTop + bar.ThumbTop) : bar.ThumbHeight / 2;
        DragMentionScrollDrag(cellY);
        return true;
    }

    /** 拖动 mention 滚动条: 由滑块位置映射到候选下标(选中项跟随窗口)。 */
    internal bool DragMentionScrollDrag(int cellY)
    {
        if (_mentionScrollGrab is not { } grab)
            return false;
        var bar = PopupList.ScrollbarOf(_mentionWindow, _mentionCandidates.Count);
        if (bar is null)
        {
            _mentionScrollGrab = null;
            return false;
        }
        var thumbRange = Math.Max(1, bar.TrackHeight - bar.ThumbHeight);
        var thumbTop = Math.Clamp(cellY - grab - bar.TrackTop, 0, thumbRange);
        var first = bar.MaxFirst == 0 ? 0 : (int)Math.Round((double)thumbTop * bar.MaxFirst / thumbRange);
        _mentionIndex = Math.Clamp(first + bar.Visible - 1, 0, Math.Max(0, _mentionCandidates.Count - 1));
        RefreshMenuStatus();
        return true;
    }

    /** 结束 mention 滚动条拖动; 返回是否曾有拖动。 */
    internal bool EndMentionScrollDrag()
    {
        if (_mentionScrollGrab is null)
            return false;
        _mentionScrollGrab = null;
        return true;
    }

    private void ConfirmCommandMenu()
    {
        var typed = Input.TrimEnd();
        if (ApplyProviderAddPreset())
        {
            RefreshMenuStatus();
            return;
        }

        var completed = CommandMenu!.Confirm();
        if (completed is null)
        {
            SyncCommandMenuInput();
            RefreshMenuStatus();
            return;
        }

        CommandMenu.Close();
        // Ctrl+P 场景: 已有提示词在选中命令后接到命令后面(命令落到首 token)。
        var text = _promptText.Length > 0 ? $"{completed} {_promptText}" : completed;
        _promptText = "";
        Input = text;
        Cursor = Input.Length;
        RefreshMenuStatus();
        // 浮层只负责"选中/确定/补全": 手敲内容已经等于浮层给出的完整命令(浮层没提供新信息)才直接发送;
        // 由浮层补全或选中参数得到的命令先回填并关浮层, 等下一次回车再发送。
        if (string.Equals(text, typed, StringComparison.Ordinal))
            Submit();
    }

    /**
     * `/provider add` 第一步选单的语义: 选中目录 provider > 带出 baseUrl/type/models(输入行为空时按 Enter 采纳);
     * 选中协议族条目(openai-compatible / anthropic / custom(...)) > 视为自定义端点, 只带出 type, provider 名仍由用户填。
     * 返回 true 表示这次回车被当作预置消费掉(不要再走 Confirm)。
     */
    private bool ApplyProviderAddPreset()
    {
        if (CommandMenu is not { Stage: CommandMenuState.MenuStage.Argument } menu
            || menu.ArgumentIndex != 0
            || !string.Equals(menu.CurrentCommand?.Name, "provider", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(menu.CurrentSubcommand?.Name, "add", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(menu.CurrentArgumentSchema?.Name, "name", StringComparison.OrdinalIgnoreCase))
            return false;

        var candidates = menu.Candidates;
        var value = candidates.Count > 0 ? candidates[menu.SelectedIndex] : menu.Query;
        if (value.Length == 0)
            return false;

        if (ProviderTypes.All.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            menu.Prefill("type", ProviderTypes.Canonical(value));
            menu.ApplyInput(menu.Prefix);
            Input = menu.Prefix;
            Cursor = Input.Length;
            return true;
        }

        if (_window.FindCatalogProvider(value) is not { } entry)
            return false;
        menu.Prefill("base-url", entry.BaseUrl);
        menu.Prefill("type", entry.Type);
        menu.Prefill("model-ids", ProviderCatalog.AllModelsMarker);
        return false;
    }

    /** Ctrl+P: 已输入提示词时唤出"能跟提示词"的命令选单; 选中后命令落到首 token, 原文本成为提示词。 */
    internal void OpenPromptCommandMenu()
    {
        var commands = _window.Ctx.Get<CommandsService>(CommandsService.ServiceName)?.List(Agent) ?? [];
        var descriptors = CommandMenuCatalog.Enrich(commands)
            .Where(command => command.AcceptsPrompt)
            .ToList();
        if (descriptors.Count == 0)
            return;

        _promptText = Input.Trim();
        CommandMenu = new CommandMenuState(descriptors, CommandCandidates);
        RefreshMenuStatus();
    }

    /** 取消 Ctrl+P 浮层时把待用提示词放回输入行, 避免用户输入被吞掉。 */
    private void RestorePromptInput()
    {
        if (_promptText.Length == 0)
            return;
        Input = _promptText;
        Cursor = Input.Length;
        _promptText = "";
    }

    private void SyncCommandMenuInput()
    {
        if (CommandMenu?.IsActive == true)
        {
            Input = CommandMenu.Prefix + CommandMenu.Query;
            Cursor = Input.Length;
        }
    }

    private void InsertMentionCandidate()
    {
        var (inserted, cursor) = MentionResolver.ReplaceActiveToken(Input, _mentionStart, _mentionEnd, _mentionCandidates[_mentionIndex]);
        Cursor = cursor;
        Input = inserted;
        CloseMentionMenu();
        RefreshMenuStatus();
    }

    private void CloseMentionMenu()
    {
        _mentionActive = false;
        _mentionCandidates = [];
        _mentionIndex = 0;
        _mentionStart = 0;
    }

    internal void RefreshMenuStatus()
    {
        if (CommandMenu?.IsActive == true)
        {
            // 有候选: 选中/补全(不发送); 无候选: 浮层没得选, 回车按输入原样发送。
            if (CommandMenu.Candidates.Count > 0)
            {
                StatusText = CommandMenu.Stage == CommandMenuState.MenuStage.Argument
                    ? $"{CommandMenu.Prompt} - ^/v 移动 - PgUp/PgDn 翻页 - 输入筛选 - Enter 选中 - Tab 下一个参数 - Esc 返回"
                    : $"{CommandMenu.Prompt} - ^/v 移动 - PgUp/PgDn 翻页 - 输入筛选 - Enter/Tab 选中 - Esc 返回";
                return;
            }

            var hasPrefill = CommandMenu.PrefilledValue(CommandMenu.ArgumentIndex) is { Length: > 0 };
            StatusText = hasPrefill
                ? $"{CommandMenu.Prompt} - Enter 采纳预置并继续 - Tab 下一个参数 - 输入可改写 - Esc 返回"
                : CommandMenu.CurrentArgumentSchema?.Required == false
                    ? $"{CommandMenu.Prompt} - Enter 跳过 - Tab 下一个参数 - 输入可填写 - Esc 返回"
                    : $"{CommandMenu.Prompt} - 必填 - 输入后 Enter - Tab 下一个参数 - Esc 返回";
            return;
        }

        if (_mentionActive)
        {
            StatusText = $"@ {_mentionCandidates.Count} candidates - ^/v Enter Esc";
            return;
        }

        SetStatusReady();
    }

    private string ReadyStatus
        => $"ready - Enter to send, ^ history, Esc cancels a running turn, {_window.KeyBindings.Keys(TuiAction.Interrupt)}×2 quits";

    private string BusyStatus => _maintaining
        ? $"compacting context {ElapsedSince(_maintenanceStartedAt)} (Esc to cancel)"
        : "working~ (Esc to cancel)";

    /** 已过时间: 用户要能看出整理还在跑, 而不是以为它早就结束了。 */
    internal static string ElapsedSince(long startedAt)
    {
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - startedAt));
        return elapsed < TimeSpan.FromMinutes(1)
            ? $"{Math.Max(1, (int)elapsed.TotalSeconds)}s"
            : $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s";
    }

    /** 有回合或整理在跑: 界面需要定时重绘, 否则时长/动画会停在第一次画出来的样子上。 */
    internal bool IsActive => Busy || _maintaining;

    public void SetStatusReady()
        => StatusText = Busy || _maintaining
            ? BusyStatus
            : _stickyStatus ?? ReadyStatus;

    /** 单行命令结果: 粘在状态行直到下一次输入或状态切换, 免得被忙碌提示立刻顶掉。 */
    private string? _stickyStatus;

    /** 上次绘制输入区时的内容宽度: 按行移动光标与这里有同一个换行口径。 */
    private int _lastInputWidth;

    /** 上下文占用跟在状态行前面; 还没有任何用量时(_context 为空)保持原样, 不给状态行添乱。 */
    private string ContextStatusOf(string status)
    {
        var segments = _stats.Segments();
        return segments.Count == 0 ? status : $"{string.Join(" | ", segments)} | {status}";
    }

    /** 整理的开始/结束单独记账, 与回合的忙碌位分开: 整理结束后不能替回合清掉忙碌。 */
    private void TrackMaintenance(ContextMaintenanceEvent maintenance)
    {
        switch (maintenance)
        {
            case ContextMaintenanceEvent.Started:
                _maintaining = true;
                _maintenanceStartedAt = Environment.TickCount64;
                SetStatusReady();
                break;
            case ContextMaintenanceEvent.Finished or ContextMaintenanceEvent.Failed:
                _maintaining = false;
                SetStatusReady();
                break;
        }
    }

    private void SetBusy(bool busy)
    {
        Busy = busy;
        SetStatusReady();
    }

    internal string CurrentCwd()
        => Agent.Session.Header.Cwd ?? Environment.CurrentDirectory;

    private void Submit()
    {
        var text = Input.Trim();
        if (text.StartsWith("/attach ", StringComparison.OrdinalIgnoreCase))
        {
            var path = text["/attach ".Length..].Trim();
            AttachImagePaths(path);
            Input = "";
            Cursor = 0;
            StatusText = _attachments.Count > 0 ? $"已附加 {_attachments.Count} 张图片 - Enter 发送" : $"无法附加图片: {path}";
            return;
        }

        // 先识别"存在图片文件路径"(绝对路径以 / 开头, 别被当成斜杠命令)。
        var consumedAsImage = false;
        if (text.Length > 0)
        {
            var remaining = AttachImagePaths(text);
            consumedAsImage = remaining.Length == 0;
            text = remaining;
        }

        if (!consumedAsImage && text.StartsWith('/'))
        {
            Input = "";
            Cursor = 0;
            _history.Add(text);
            _historyIndex = -1;
            _window.RunSlashCommand(this, text);
            return;
        }

        if (text.Length == 0 && _attachments.Count == 0)
            return;
        Input = "";
        Cursor = 0;
        if (text.Length > 0)
            _history.Add(text);
        _historyIndex = -1;

        var displayBlocks = new List<ContentBlock>();
        if (text.Length > 0)
            displayBlocks.Add(new TextBlock(text));
        foreach (var attachment in _attachments)
            displayBlocks.Add(new ImageBlock(attachment));
        if (displayBlocks.Count == 0)
            displayBlocks.Add(new TextBlock("[image]"));
        // 忙碌时这一条只是排进 inbox: 标成"已排队", 别让它看起来已经发出去了。
        if (Busy || _maintaining)
            Renderer.AppendQueuedUserMessage(MessageFactory.CreateUserMessage(displayBlocks));
        else
            Renderer.AppendUserMessage(MessageFactory.CreateUserMessage(displayBlocks));
        RequestStickToBottom();

        SendUserText(text);
    }

    /**
     * 消息里可作为附件抽取的图片路径: 要么 `@<相对或绝对路径>`(按会话工作区解析), 要么独立的绝对路径(盘符或 / 开头)。
     * `(?<!\S)` 边界保证不会把 `@foo/bar.png` 里的 `/bar.png` 片段误当成独立路径。
     */
    private static readonly Regex ImagePathPattern = new(
        @"(?<!\S)(?:@(?<m>[^\s@""']*?\.(?:png|jpe?g|gif|webp|avif))|(?<p>(?:[A-Za-z]:[\\/]|/)[^\s@""']*?\.(?:png|jpe?g|gif|webp|avif)))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /**
     * 从消息里抽出可读的本地图片路径并挂为附件、从文本中移除, 返回剩余文本(通常是用户指令)。
     * 这样"粘贴图片路径/@图片 + 指令"也能把图片作为 ImageBlock 发给模型, 而不是把路径当正文。
     */
    internal string AttachImagePaths(string text)
    {
        var remaining = text;
        try
        {
            foreach (Match match in ImagePathPattern.Matches(text))
            {
                var relative = match.Groups["m"].Success;
                var candidate = relative ? match.Groups["m"].Value : match.Groups["p"].Value;
                if (!TryAttachFile(candidate, relative))
                    continue;
                remaining = remaining.Replace(match.Value, " ", StringComparison.Ordinal);
            }
        }
        catch (Exception)
        {
            // 依赖缺失/读取失败: 退化为文本, 绝不崩。
        }

        return Regex.Replace(remaining.Trim(), " {2,}", " ");
    }

    private bool TryAttachFile(string candidate, bool relativeToCwd)
    {
        try
        {
            var path = NormalizePath(candidate);
            if (path is null)
                return false;
            if (relativeToCwd && !Path.IsPathRooted(path))
                path = Path.GetFullPath(Path.Combine(CurrentCwd(), path));
            if (!File.Exists(path))
                return false;
            if (ImageAttachments.MediaTypeForExtension(Path.GetExtension(path)) is not { } mediaType)
                return false;
            var store = Agent.Ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName, false);
            if (store is null)
                return false;
            var bytes = File.ReadAllBytes(path);
            _attachments.Add(store.Put(bytes, mediaType, 0, 0, Path.GetFileName(path)));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /** 仅供测试: 返回文本里匹配到的图片路径(去掉 @ 前缀, 不访问文件系统)。 */
    internal static IReadOnlyList<string> MatchImagePathTokens(string text)
        => [.. ImagePathPattern.Matches(text).Select(match =>
            match.Groups["m"].Success ? match.Groups["m"].Value : match.Groups["p"].Value)];

    /** 文本里是否含"图片路径"片段(用于决定不把它当斜杠命令/不做命令补全)。 */
    private static bool IsImagePath(string text)
    {
        try
        {
            return ImagePathPattern.IsMatch(text);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string? NormalizePath(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;
        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new Uri(trimmed).LocalPath;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            trimmed = trimmed[1..^1];

        if (OperatingSystem.IsWindows())
        {
            // 终端可能给 MSYS/Git-Bash 风格: /C:/Users/... 或 /c/Users/... -- 还原成 C:\Users\...
            if (trimmed.Length >= 3 && trimmed[0] == '/' && char.IsLetter(trimmed[1]) && trimmed[2] == ':')
                trimmed = trimmed[1..];
            else if (trimmed.Length >= 3 && trimmed[0] == '/' && char.IsLetter(trimmed[1]) && trimmed[2] == '/')
                trimmed = $"{trimmed[1]}:{trimmed[2..]}";
            if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
                trimmed = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
            trimmed = trimmed.Replace('/', '\\');
        }

        return trimmed;
    }

    /** 把文本作为用户消息发出: 普通输入与"命令 + 提示词"的提示词共用同一条路径。 */
    internal void SendUserText(string text)
    {
        if (text.Length == 0 && _attachments.Count == 0)
            return;
        var expandedText = text.Length == 0
            ? ""
            : MentionResolver.ExpandMentions(text, _window.CurrentSessions());
        var content = new List<ContentBlock>();
        if (expandedText.Length > 0)
            content.Add(new TextBlock(expandedText));
        foreach (var attachment in _attachments)
            content.Add(new ImageBlock(attachment));
        _attachments.Clear();
        SetBusy(true);
        Agent.Followup(MessageFactory.CreateUserMessage(content));
    }

    /**
     * 需要在视觉行之间移动光标: 多行草稿与"很长的单行草稿(软折行)"都算。
     * 返回 true 表示这次按键已被"按行移动"消费; 移动不了(已在首行/末行)才让位给历史记录。
     */
    private bool MoveCursorByRow(int delta)
    {
        var width = _lastInputWidth > 0 ? _lastInputWidth : Math.Max(1, 80 - InputPrompt.Length);
        var caret = InputLayout.Cursor(Input, Cursor, width);
        var target = caret.Row + delta;
        var rows = InputLayout.Wrap(Input, width);
        if (target < 0 || target >= rows.Count)
            return false;
        Cursor = InputLayout.OffsetAt(Input, target, caret.Column, width);
        return true;
    }

    /**
     * ↑/↓ 切历史: 只由"光标在草稿开头/末尾"触发(见 HandleKey), 所以这里的每次调用都是用户的明确意图。
     * 离开草稿时留一份底(内容+光标), 按 ↓ 回到 -1 时恢复它 —— 否则"看一眼历史"就会把正在写的东西清空。
     */
    private void RecallHistory(int direction)
    {
        if (_history.Count == 0)
            return;
        // 历史是一条链, 当前草稿挂在新的一头: ↑ 往前走(最早一条为止), ↓ 走回草稿(-1)。
        var next = _historyIndex < 0
            ? direction < 0 ? _history.Count - 1 : -1
            : direction < 0
                ? Math.Max(0, _historyIndex - 1)
                : _historyIndex >= _history.Count - 1 ? -1 : _historyIndex + 1;
        if (next == _historyIndex)
            return;
        if (_historyIndex < 0)
        {
            _draftBeforeHistory = Input;
            _draftCursorBeforeHistory = Cursor;
        }
        _historyIndex = next;
        if (_historyIndex < 0)
        {
            Input = _draftBeforeHistory;
            Cursor = Math.Min(_draftCursorBeforeHistory, Input.Length);
            _draftBeforeHistory = "";
            _draftCursorBeforeHistory = 0;
            return;
        }
        Input = _history[_historyIndex];
        Cursor = Input.Length;
    }

    private void SubscribeSessionRenamed()
    {
        if (_sessionRenamedSubscribed)
            return;
        Agent.Session.Renamed += HandleSessionRenamed;
        _sessionRenamedSubscribed = true;
    }

    private void UnsubscribeSessionRenamed()
    {
        if (!_sessionRenamedSubscribed)
            return;
        Agent.Session.Renamed -= HandleSessionRenamed;
        _sessionRenamedSubscribed = false;
    }

    private void HandleSessionRenamed(Session session, SessionHeader header)
        => _window.QueueAction(_window.InvalidateSessionInfos);

    private void InvalidateWrapCache()
    {
        _wrapCacheVersion = -1;
        _wrapCacheWidth = -1;
        _wrapRows = null;
        _wrapProcessedLine = 0;
        _visualRows.Clear();
        _foldSnapshot.Clear();
    }

    private int ColumnToCharIndex(int column)
    {
        var current = 0;
        for (var index = 0; index < Input.Length; index++)
        {
            var width = TerminalTextWidth.Of(Input[index]);
            if (current + width > column)
                return index;
            current += width;
        }

        return Input.Length;
    }

    /** 指针按下: 锚定选择起点(阶段 3.2 拖动选择); 单击不拖动时在释放阶段判空。 */
    public void BeginSelection(int cellX, int cellY, ConsoleRect rect)
    {
        var (row, column) = ToSelectionPoint(cellX, cellY, rect);
        _selectionAnchorRow = row;
        _selectionAnchorColumn = column;
        _selectionRow = row;
        _selectionColumn = column;
        _selectionActive = true;
        StickToBottom = false;
    }

    /** 拖动: 移动选择游标。 */
    public void ExtendSelection(int cellX, int cellY, ConsoleRect rect)
    {
        if (!_selectionActive)
            return;
        var (row, column) = ToSelectionPoint(cellX, cellY, rect);
        _selectionRow = row;
        _selectionColumn = column;
    }

    /** 释放: 结束选择并返回选中文本(空选择返回 null, 调用方据此决定是否写剪贴板)。 */
    public string? FinishSelection()
    {
        var text = HasNonEmptySelection() ? ExtractSelection() : null;
        _selectionActive = false;
        return text;
    }

    public void ClearSelection() => _selectionActive = false;

    private (int Row, int Column) ToSelectionPoint(int cellX, int cellY, ConsoleRect rect)
    {
        var row = ScrollOffset + Math.Clamp(cellY - rect.Y, 0, Math.Max(0, rect.Height - 1));
        var column = Math.Clamp(cellX - rect.X, 0, Math.Max(0, rect.Width - 1));
        return (row, column);
    }

    private bool HasNonEmptySelection()
        => _selectionActive && (_selectionAnchorRow != _selectionRow || _selectionAnchorColumn != _selectionColumn);

    private (int FirstRow, int FirstColumn, int LastRow, int LastColumn) OrderedSelection()
    {
        var anchorBeforeCursor = _selectionAnchorRow < _selectionRow
            || (_selectionAnchorRow == _selectionRow && _selectionAnchorColumn <= _selectionColumn);
        return anchorBeforeCursor
            ? (_selectionAnchorRow, _selectionAnchorColumn, _selectionRow, _selectionColumn)
            : (_selectionRow, _selectionColumn, _selectionAnchorRow, _selectionAnchorColumn);
    }

    private string? ExtractSelection()
    {
        var rows = _wrapRows;
        if (rows is null || rows.Count == 0)
            return null;
        var (firstRow, firstColumn, lastRow, lastColumn) = OrderedSelection();
        firstRow = Math.Max(0, firstRow);
        lastRow = Math.Min(rows.Count - 1, lastRow);
        if (lastRow < firstRow)
            return null;
        var builder = new StringBuilder();
        for (var row = firstRow; row <= lastRow; row++)
        {
            var line = RowSpan(rows[row]);
            var start = row == firstRow ? Math.Min(firstColumn, line.Length) : 0;
            var end = row == lastRow ? Math.Min(lastColumn + 1, line.Length) : line.Length;
            if (end > start)
                builder.Append(line[start..end]);
            if (row < lastRow)
                builder.Append('\n');
        }
        var text = builder.ToString().TrimEnd(' ', '\n');
        return text.Length == 0 ? null : text;
    }

    /** diff 卡片行着色(红删绿增 + 标题强调), 与 GUI diff 卡片语义一致; 其它行不着色。 */
    private void ApplyTintHighlight(CellGrid grid, ConsoleRect rect, int row, int wrappedIndex)
    {
        var offset = SourceOffsetAt(wrappedIndex);
        if (offset is null)
            return;
        var lineIndex = LineIndexOf(offset.Value);
        var tints = Renderer.LineTints;
        if (lineIndex < 0 || lineIndex >= tints.Count)
            return;
        var (foreground, background, style) = tints[lineIndex] switch
        {
            TranscriptTint.DiffAdded => (CellColor.FromPalette(AnsiColor.Black), CellColor.FromPalette(AnsiColor.Green), CellStyle.None),
            TranscriptTint.DiffRemoved => (CellColor.FromPalette(AnsiColor.BrightWhite), CellColor.FromPalette(AnsiColor.Red), CellStyle.None),
            TranscriptTint.DiffTitle => (CellColor.FromPalette(AnsiColor.BrightCyan), CellColor.Default, CellStyle.Bold),
            TranscriptTint.Error => (TuiTheme.Error, CellColor.Default, CellStyle.None),
            _ => (CellColor.Default, CellColor.Default, CellStyle.None),
        };
        if (foreground.IsDefault && background.IsDefault && style == CellStyle.None)
            return;
        for (var column = 0; column < rect.Width; column++)
        {
            var x = rect.X + column;
            if (x < 0 || x >= grid.Width)
                continue;
            var cell = grid[x, rect.Y + row];
            grid[x, rect.Y + row] = new Cell(cell.Character, foreground, background, style | (cell.Style & CellStyle.Literal));
        }
    }

    /** 选择高亮: 反显样式, CPU/GPU 两路渲染自动同效。 */
    private void ApplySelectionHighlight(CellGrid grid, ConsoleRect rect, int row, int lineIndex)
    {
        if (!HasNonEmptySelection())
            return;
        var (firstRow, firstColumn, lastRow, lastColumn) = OrderedSelection();
        if (lineIndex < firstRow || lineIndex > lastRow)
            return;
        var start = lineIndex == firstRow ? firstColumn : 0;
        var end = lineIndex == lastRow ? lastColumn : rect.Width - 1;
        for (var column = Math.Max(0, start); column <= Math.Min(end, rect.Width - 1); column++)
        {
            var x = rect.X + column;
            if (x < 0 || x >= grid.Width)
                continue;
            var cell = grid[x, rect.Y + row];
            grid[x, rect.Y + row] = new Cell(cell.Character, cell.Foreground, cell.Background, cell.Style | CellStyle.Reverse);
        }
    }

    /**
     * 折叠头行点击 = 切换折叠(展开↔收起), 展开的分页折叠正文行点击 = 翻下一页。
     * 分开的理由: 头行点击若也参与翻页, 用户必须翻完所有页才能收起(实测很难受)。
     */
    public bool TryToggleFoldAt(int cellY, ConsoleRect rect)
    {
        var wrappedIndex = ScrollOffset + (cellY - rect.Y);
        var offset = SourceOffsetAt(wrappedIndex);
        if (offset is null)
            return false;
        var header = Renderer.Folds.FirstOrDefault(candidate => candidate.Start == offset.Value && candidate.End > candidate.Start);
        if (header is not null)
        {
            SetFoldCollapsed(header, !header.Collapsed);
            return true;
        }
        var body = Renderer.Folds.FirstOrDefault(candidate =>
            !candidate.Collapsed && candidate.Paged && candidate.Start < offset.Value && offset.Value < candidate.End);
        if (body is null)
            return false;
        PageFold(body);
        return true;
    }

    /** 只改折叠态(不动滚动位置): 展开从头开始看, 收起清掉页码。 */
    private void SetFoldCollapsed(TranscriptFold fold, bool collapsed)
    {
        fold.Collapsed = collapsed;
        if (collapsed || fold.Scroll >= fold.Lines)
            fold.Scroll = 0;
        Renderer.BumpVersion();
    }

    /** 翻到下一页; 已在最后一页时回到第一页(循环, 不会卡在末页)。 */
    private void PageFold(TranscriptFold fold)
    {
        var step = DiffCardDisplay.PageLines;
        var next = fold.Scroll + step;
        fold.Scroll = next + step > fold.Lines ? 0 : next;
        Renderer.BumpVersion();
    }

    /** 键盘(Enter)与鼠标点击共用同一切换入口: 展开 → 逐页翻 → 收起。 */
    public void ToggleFold(TranscriptFold fold)
    {
        if (fold.Collapsed)
        {
            fold.Collapsed = false;
            fold.Scroll = 0;
        }
        else if (fold.Paged && fold.Scroll + DiffCardDisplay.PageLines < fold.Lines)
        {
            fold.Scroll += DiffCardDisplay.PageLines;
        }
        else
        {
            fold.Collapsed = true;
            fold.Scroll = 0;
        }
        Renderer.BumpVersion();
    }

    /**
     * 滚轮落在某个已展开的长折叠里时就滚折叠本身, 不再带动整篇记录。
     * 到边界后返回 false, 让滚轮继续滚正文(用户不会觉得滚轮"卡住")。
     */
    public bool ScrollFoldAt(float delta, int cellY, ConsoleRect rect)
    {
        if (Math.Abs(delta) < float.Epsilon)
            return false;
        var wrappedIndex = ScrollOffset + (cellY - rect.Y);
        if (wrappedIndex < 0 || SourceOffsetAt(wrappedIndex) is not { } offset)
            return false;
        var fold = Renderer.Folds.FirstOrDefault(candidate =>
            !candidate.Collapsed && candidate.Paged && candidate.Start <= offset && offset < candidate.End);
        if (fold is null)
            return false;
        var maxScroll = Math.Max(0, fold.Lines - DiffCardDisplay.PageLines);
        var target = Math.Clamp(fold.Scroll - _wheel.Scroll(delta), 0, maxScroll);
        if (target == fold.Scroll)
            return false;
        fold.Scroll = target;
        Renderer.BumpVersion();
        return true;
    }

    /** 视觉行 > 源偏移: rows 的 FlatStart 升序, 二分找最后一个不超过 wrappedIndex 的行。 */
    private int? SourceOffsetAt(int wrappedIndex)
    {
        var rows = _visualRows;
        if (_wrapRows is null || rows.Count == 0 || wrappedIndex < 0 || wrappedIndex >= _wrapRows.Count)
            return null;
        var low = 0;
        var high = rows.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (rows[middle].FlatStart <= wrappedIndex)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found < 0 ? null : rows[found].SourceStart;
    }

    private TranscriptFold? FindSelectedFold()
        => SelectedFoldKey is null ? null : Renderer.Folds.FirstOrDefault(fold => FoldKey(fold) == SelectedFoldKey);

    private void SelectNextFold(int direction)
    {
        var folds = Renderer.Folds;
        if (folds.Count == 0)
        {
            SelectedFoldKey = null;
            return;
        }

        var currentIndex = -1;
        for (var index = 0; index < folds.Count; index++)
        {
            if (FoldKey(folds[index]) == SelectedFoldKey)
            {
                currentIndex = index;
                break;
            }
        }

        var next = (currentIndex + direction + folds.Count) % folds.Count;
        SelectedFoldKey = FoldKey(folds[next]);
    }

    private static string FoldKey(TranscriptFold fold)
        => $"{fold.Start}:{fold.Label}";

    /** 视觉行(值类型): 源偏移 + 它在折行列表里的下标。 */
    private readonly record struct VisualRow(int SourceStart, int FlatStart);

    /**
     * 折行行: 文本按 [Start, Start + Length) 从转录正文取一份切片, 只有折叠头/页码这类合成内容才自带字符串。
     * 逐行存 string 会把同一段文字再抄一份 —— 用户往上滚过的页越多, 这份重复越大。
     */
    private readonly record struct WrapRow(int Start, int Length, string? Literal);

    private ReadOnlySpan<char> RowSpan(WrapRow row)
        => row.Literal is { } literal ? literal.AsSpan() : Renderer.TextSpan(row.Start, row.Length);

    private void RebuildWrapCache(int width)
    {
        var lineCount = Renderer.LineCount;
        var folds = Renderer.Folds;
        var from = 0;
        if (_wrapRows is not null && _wrapCacheWidth == width && _wrapProcessedLine <= lineCount + 1)
        {
            from = _wrapProcessedLine;
            for (var index = 0; index < folds.Count; index++)
            {
                var fold = folds[index];
                if (index < _foldSnapshot.Count && _foldSnapshot[index] == (fold.Start, fold.End, fold.Collapsed, fold.Scroll))
                    continue;
                if (index >= _foldSnapshot.Count && !fold.Collapsed)
                    continue;
                from = Math.Min(from, LineIndexOf(fold.Start));
            }
        }
        RebuildWrapFromLines(folds, width, from);
        SnapshotFolds(folds);
        _wrapCacheWidth = width;
    }

    /** 行索引空间的增量重排: 行文本按切片从 renderer 取; fold 偏移经行起点二分映射为行号。 */
    private void RebuildWrapFromLines(IReadOnlyList<TranscriptFold> folds, int width, int fromLine)
    {
        var rows = _visualRows;
        var wrapped = _wrapRows ??= [];
        var lineCount = Renderer.LineCount;
        var fromOffset = fromLine < lineCount ? Renderer.LineAt(fromLine).Start : Renderer.TailStart;
        var rowIndex = LowerBoundRow(rows, fromOffset);
        var flatIndex = rowIndex < rows.Count ? rows[rowIndex].FlatStart : wrapped.Count;
        if (rowIndex < rows.Count)
            rows.RemoveRange(rowIndex, rows.Count - rowIndex);
        wrapped.RemoveRange(flatIndex, wrapped.Count - flatIndex);
        WrapInto(Renderer, folds, width, fromLine, lineCount + 1);
        _wrapProcessedLine = lineCount;
    }

    /**
     * 把 [fromLine, lastLine) 这些逻辑行折成视觉行, 追加到 `_visualRows` / `_wrapRows` 末尾。
     * lastLine 取 lineCount + 1 时把"尾部半行"那一行也算进来。
     * 前置一页时也走这里(见 PrependWrapCache): 折行只依赖行文本与宽度, 页与页之间互不影响。
     */
    private void WrapInto(TranscriptRenderer source, IReadOnlyList<TranscriptFold> folds, int width, int fromLine, int lastLine)
    {
        var lineCount = source.LineCount;
        var tailStart = source.TailStart;
        // 折行时每行都要查"展开折叠的头", 所以先按起点建好索引(见 ExpandedFoldsByStart)。
        var expandedFolds = ExpandedFoldsByStart(folds);
        var collapsedFolds = CollapsedFoldsByStart(folds);
        var lineIndex = fromLine;
        while (lineIndex < lastLine)
        {
            var lineStart = lineIndex < lineCount ? source.LineAt(lineIndex).Start : tailStart;
            var raw = lineIndex < lineCount ? source.LineSpan(lineIndex) : source.TailSpan;
            var expanded = expandedFolds.GetValueOrDefault(lineStart);
            if (expanded is not null)
            {
                var endLine = LineIndexOf(source, expanded.End);
                // 折叠内容可能整段落在同一行里(单行 thinking、行内代码围栏、折叠起点与终点同处一行):
                // 这时 endLine 就是当前行, 以它作下一行会让外层循环原地打转(TUI 100% CPU 卡死、按键无响应)。
                // 这种退化情形按"这一行就是折叠体"渲染, 保证至少前进一行。
                if (endLine <= lineIndex)
                {
                    var inline = raw.EndsWith('\r') ? raw[..^1] : raw;
                    AddRow(lineStart, inline, width);
                    lineIndex += 1;
                    continue;
                }

                var foldLines = Math.Max(0, endLine - lineIndex);
                expanded.Lines = foldLines;
                var pageCount = !expanded.Paged
                    ? 1
                    : Math.Max(1, (foldLines + DiffCardDisplay.PageLines - 1) / DiffCardDisplay.PageLines);
                expanded.PageCount = pageCount;
                var maxScroll = expanded.Paged ? Math.Max(0, foldLines - DiffCardDisplay.PageLines) : 0;
                expanded.Scroll = Math.Clamp(expanded.Scroll, 0, maxScroll);
                if (expanded.Paged && foldLines > DiffCardDisplay.PageLines)
                {
                    var last = Math.Min(foldLines, expanded.Scroll + DiffCardDisplay.PageLines);
                    AddLiteralRow(expanded.Start,
                        $"{FoldHeader(expanded)} [第 {expanded.Scroll + 1}-{last} 行 / 共 {foldLines} 行]  (滚轮滚动, 点正文翻页)",
                        width);
                }
                var bodyStart = expanded.Paged ? lineIndex + expanded.Scroll : lineIndex;
                var bodyEnd = expanded.Paged ? Math.Min(endLine, bodyStart + DiffCardDisplay.PageLines) : endLine;
                for (var pageLine = bodyStart; pageLine < bodyEnd; pageLine++)
                {
                    var pageStartOffset = pageLine < lineCount ? source.LineAt(pageLine).Start : tailStart;
                    var pageContent = pageLine < lineCount ? source.LineSpan(pageLine) : source.TailSpan;
                    if (pageContent.EndsWith('\r'))
                        pageContent = pageContent[..^1];
                    if (expanded.Paged && pageLine == bodyStart && expanded.Scroll == 0 && pageCount > 1)
                        AddLiteralRow(pageStartOffset, $"{pageContent} [1/{pageCount}]", width);
                    else
                        AddRow(pageStartOffset, pageContent, width);
                }
                lineIndex = Math.Max(endLine, lineIndex + 1);
                continue;
            }
            var fold = CollapsedFoldAt(collapsedFolds, lineStart);
            if (fold is not null)
            {
                AddLiteralRow(lineStart, FoldHeader(fold), width);
                var endLine = LineIndexOf(source, fold.End);
                var endLineStart = endLine < lineCount ? source.LineAt(endLine).Start : tailStart;
                if (fold.End <= endLineStart)
                {
                    // 与展开态同一个不变量: 下一行必须比当前行靠后, 否则外层循环不前进。
                    lineIndex = Math.Max(endLine, lineIndex + 1);
                }
                else
                {
                    var endRaw = endLine < lineCount ? source.LineSpan(endLine) : source.TailSpan;
                    var rest = endRaw[(fold.End - endLineStart)..];
                    if (rest.EndsWith('\r'))
                        rest = rest[..^1];
                    AddRow(fold.End, rest, width);
                    lineIndex = endLine + 1;
                }
                continue;
            }
            var content = raw.EndsWith('\r') ? raw[..^1] : raw;
            AddRow(lineStart, content, width);
            lineIndex++;
        }
    }

    /** offset > 行号: 行 i 覆盖 [start_i, 下一行起点), 尾部行覆盖 [TailStart, 末尾)。 */
    private int LineIndexOf(int offset) => LineIndexOf(Renderer, offset);

    private static int LineIndexOf(TranscriptRenderer source, int offset)
    {
        var lineCount = source.LineCount;
        var tailStart = source.TailStart;
        if (offset >= tailStart)
            return lineCount;
        var low = 0;
        var high = lineCount;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (source.LineAt(mid).Start <= offset)
                low = mid + 1;
            else
                high = mid;
        }
        return Math.Max(0, low - 1);
    }

    /** 折叠头: ▸ 已折叠 / ▾ 展开中 —— 三角是"可展开"的通行写法, 点它或标题文字都切换折叠。 */
    private static string FoldHeader(TranscriptFold fold)
        => $"{(fold.Collapsed ? "▸" : "▾")} {fold.Preview.TrimStart()}";

    /**
     * 折叠态折叠按起点排好的数组: 折行时每行都要问"这行落在哪个折叠里", 线性扫全部折叠是 O(行数×折叠数)。
     * 先二分定位, 再向前回溯"还覆盖这里的" —— 通常一步就结束。
     */
    private static TranscriptFold[] CollapsedFoldsByStart(IReadOnlyList<TranscriptFold> folds)
    {
        var collapsed = new List<TranscriptFold>(folds.Count);
        foreach (var fold in folds)
        {
            if (fold.Collapsed)
                collapsed.Add(fold);
        }
        // 不依赖"折叠总是按起点递增追加"这个隐含约定: 前置/合并之后照样二分得对。
        collapsed.Sort((left, right) => left.Start.CompareTo(right.Start));
        return [.. collapsed];
    }

    /** 覆盖 position 的最外层折叠态折叠(嵌套时取起点最小的那个)。 */
    private static TranscriptFold? CollapsedFoldAt(TranscriptFold[] sortedByStart, int position)
    {
        var low = 0;
        var high = sortedByStart.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (sortedByStart[middle].Start <= position)
                low = middle + 1;
            else
                high = middle;
        }
        TranscriptFold? outermost = null;
        for (var index = low - 1; index >= 0 && position < sortedByStart[index].End; index -= 1)
        {
            if (position >= sortedByStart[index].Start)
                outermost = sortedByStart[index];
        }
        return outermost;
    }

    /** 展开态的 fold 只渲染当前页; 折叠头所在的源偏移按 fold.Start 命中。 */
    private static TranscriptFold? FindExpandedFold(IReadOnlyList<TranscriptFold> folds, int position)
    {
        foreach (var fold in folds)
        {
            if (!fold.Collapsed && fold.Start == position && fold.End > fold.Start)
                return fold;
        }
        return null;
    }

    /**
     * 展开态折叠按起点建索引: 折行时每一行都要问"这行的起点是不是某个展开折叠的头",
     * 线性扫全部折叠会让整段折行变成 O(行数×折叠数)(11.5MB/17.9 万行/5 千折叠要 1.6 秒)。
     * 折叠在转录里按起点递增追加, 起点即标识, 所以按起点建表即可精确命中。
     */
    private static Dictionary<int, TranscriptFold> ExpandedFoldsByStart(IReadOnlyList<TranscriptFold> folds)
    {
        var map = new Dictionary<int, TranscriptFold>(folds.Count);
        foreach (var fold in folds)
        {
            if (!fold.Collapsed && fold.End > fold.Start)
                map[fold.Start] = fold;
        }
        return map;
    }

    private static int LowerBoundRow(List<VisualRow> rows, int sourceStart)
    {
        var low = 0;
        var high = rows.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (rows[middle].SourceStart < sourceStart)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    /** 一逻辑行折成若干视觉行; 文本留在转录正文里, 这里只记区间。 */
    private void AddRow(int sourceStart, ReadOnlySpan<char> content, int width)
    {
        _visualRows.Add(new VisualRow(sourceStart, _wrapRows!.Count));
        if (content.Length == 0)
        {
            _wrapRows.Add(new WrapRow(sourceStart, 0, null));
            return;
        }

        // 逻辑行里可能内嵌换行(session-query 等多行命令输出): 必须先按 \n/\r 切行,
        // 否则 '\n' 会被当普通字符写进网格, 终端渲染时真的换行, 导致后续内容错位/互相插入。
        var start = 0;
        var wrote = false;
        for (var index = 0; index <= content.Length; index++)
        {
            if (index < content.Length && content[index] is not ('\n' or '\r'))
                continue;
            if (index > start)
            {
                WrapSegment(content[start..index], sourceStart + start, width);
                wrote = true;
            }

            start = index + 1;
        }

        if (!wrote)
            _wrapRows.Add(new WrapRow(sourceStart, 0, null));
    }

    /** 合成内容的视觉行(折叠头/页码): 它不来自转录正文, 只能自己带字符串。 */
    private void AddLiteralRow(int sourceStart, string content, int width)
    {
        _visualRows.Add(new VisualRow(sourceStart, _wrapRows!.Count));
        var pieces = new List<string>();
        WrapSingleLine(content, width, pieces);
        foreach (var piece in pieces)
            _wrapRows.Add(new WrapRow(0, 0, piece));
    }

    private void SnapshotFolds(IReadOnlyList<TranscriptFold> folds)
    {
        _foldSnapshot.Clear();
        foreach (var fold in folds)
            _foldSnapshot.Add((fold.Start, fold.End, fold.Collapsed, fold.Scroll));
    }

    internal static void WrapSingleLine(string line, int width, List<string> output)
    {
        if (line.Length == 0)
        {
            output.Add("");
            return;
        }

        // 逻辑行里可能内嵌换行(session-query 等多行命令输出): 必须先按 \n/\r 切行,
        // 否则 '\n' 会被当普通字符写进网格, 终端渲染时真的换行, 导致后续内容错位/互相插入。
        var start = 0;
        var wrote = false;
        for (var index = 0; index <= line.Length; index++)
        {
            if (index < line.Length && line[index] is not ('\n' or '\r'))
                continue;
            if (index > start)
            {
                WrapSegment(line[start..index], width, output);
                wrote = true;
            }

            start = index + 1;
        }

        if (!wrote)
            output.Add("");
    }

    /** 把一段(已按 \n/\r 切开)的文本按显示宽度切行, 每行记成转录正文里的 [sourceStart + 起, 长度) 区间。 */
    private void WrapSegment(ReadOnlySpan<char> segment, int sourceStart, int width)
    {
        var start = 0;
        var column = 0;
        for (var index = 0; index < segment.Length; index++)
        {
            var characterWidth = TerminalTextWidth.Of(segment[index]);
            if (column + characterWidth > width)
            {
                _wrapRows!.Add(new WrapRow(sourceStart + start, index - start, null));
                start = index;
                column = 0;
            }

            column += characterWidth;
        }

        _wrapRows!.Add(new WrapRow(sourceStart + start, segment.Length - start, null));
    }

    private static void WrapSegment(string segment, int width, List<string> output)
    {
        var start = 0;
        var column = 0;
        for (var index = 0; index < segment.Length; index++)
        {
            var characterWidth = TerminalTextWidth.Of(segment[index]);
            if (column + characterWidth > width)
            {
                output.Add(segment[start..index]);
                start = index;
                column = 0;
            }

            column += characterWidth;
        }

        output.Add(segment[start..]);
    }

    public void Dispose()
    {
        UnsubscribeSessionRenamed();
        PendingApproval?.TrySetResult(ApprovalOutcome.Cancelled);
        PendingQuestions?.TrySetResult(AskUserQuestionAnswer.Dismissed);
    }
}




