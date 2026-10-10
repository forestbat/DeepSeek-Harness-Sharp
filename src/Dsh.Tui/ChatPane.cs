using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tui;

/**
 * 单个 agent 绑定的视图状态: 渲染器、滚动/输入缓冲、fold 选择、菜单与待决交互。
 * ChatWindow 持有 pane 集合与分割树, 键盘只进焦点 pane; 后台 pane 只更新自己的渲染器。
 */
public sealed class ChatPane : ITuiPane
{
    private const string InputPrompt = "> ";
    private const string ReadyStatus = "ready - Enter to send, ^ history, Esc cancels a running turn, Ctrl+C×2 quits";

    private readonly ChatWindow _window;
    private readonly Dictionary<ToolCallId, (int Start, string Arguments)> _pendingSubagentCalls = [];
    private AskUserQuestionRequest? _questionRequest;
    private readonly List<AskUserQuestionAnswerItem> _questionAnswers = [];
    private readonly HashSet<int> _questionSelection = [];
    private int _questionIndex;
    private string? _stashedInput;
    private int _stashedCursor;
    private IReadOnlyList<string> _mentionCandidates = [];
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
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private bool _sessionRenamedSubscribed;
    private int _wrapCacheVersion = -1;
    private int _wrapCacheWidth = -1;
    private List<string>? _wrapCacheLines;
    private int _wrapProcessedLine;
    private readonly List<VisualRow> _visualRows = [];
    private readonly List<(int Start, int End, bool Collapsed)> _foldSnapshot = [];

    public ChatPane(ChatWindow window, int id, AgentLoopAgent agent)
    {
        _window = window;
        Id = id;
        Agent = agent;
        SubscribeSessionRenamed();
        ReplayEvents();
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
    public bool HasOverlay => CommandMenu?.IsActive == true || _mentionActive;

    public void DrawOverlay(CellGrid grid, ConsoleRect rect) => DrawMenuOverlay(grid, rect);

    public string Input { get; set; } = "";

    public int Cursor { get; set; }

    public int ScrollOffset { get; set; }

    /** 滚到包含指定文本的首行(供 /timestamp jump): 需先绘制过一次以建立视口缓存。 */
    internal bool TryScrollToMessage(string needle)
    {
        if (string.IsNullOrEmpty(needle) || _wrapCacheLines is not { Count: > 0 } lines)
            return false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Contains(needle, StringComparison.Ordinal))
            {
                StickToBottom = false;
                ScrollOffset = index;
                _suppressStickOnce = true;
                return true;
            }
        }
        return false;
    }

    public bool StickToBottom { get; set; } = true;

    /** jump 后抑制一次“新内容贴底”, 让目标消息保持在顶上; 一次性, 之后恢复正常贴底。 */
    private bool _suppressStickOnce;

    private void RequestStickToBottom()
    {
        if (_suppressStickOnce)
        {
            _suppressStickOnce = false;
            return;
        }

        StickToBottom = true;
    }

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

    public string StatusText { get; set; } = ReadyStatus;

    public string? DeleteConfirmSessionId { get; set; }

    public int CursorScreenX { get; private set; }

    public int CursorScreenY { get; private set; }

    public void HandleKey(ConsoleKeyInfo key)
    {
        if (PendingApproval is not null)
        {
            if (key.Key == ConsoleKey.Y)
                AnswerApproval(ApprovalOutcome.AllowedOnce);
            else if (key.Key == ConsoleKey.N)
                AnswerApproval(ApprovalOutcome.Rejected);
            else if (key.Key == ConsoleKey.C || key.Key == ConsoleKey.Escape)
                AnswerApproval(ApprovalOutcome.Cancelled);
            return;
        }

        if (PendingQuestions is not null)
        {
            HandleQuestionKey(key);
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
                    var candidate = CommandMenu.Candidates.ElementAtOrDefault(CommandMenu.SelectedIndex);
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
            case ConsoleKey.Enter:
                Submit();
                break;
            case ConsoleKey.Escape:
                if (Busy)
                    Agent.Cancel(new AgentCancelCause.User());
                break;
            case ConsoleKey.UpArrow:
                RecallHistory(-1);
                break;
            case ConsoleKey.DownArrow:
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
                break;
            case ConsoleKey.PageDown:
                ScrollOffset += 10;
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

    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text) || PendingApproval is not null)
            return;
        var sanitized = text.Replace('\r', ' ').Replace('\n', ' ');
        if (sanitized.Length == 0)
            return;
        Input = Input.Insert(Cursor, sanitized);
        Cursor += sanitized.Length;
        RefreshMenus();
    }

    /** 括号粘贴: 本地图片路径转附件; 空粘贴尝试读 OS 剪贴板图片; 其余按文本插入。 */
    internal void HandlePaste(string text)
    {
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
        InsertText(remaining);
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
        if (PendingApproval is not null)
            return;
        var lines = _wheel.Scroll(delta);
        if (lines > 0)
        {
            StickToBottom = false;
            ScrollOffset = Math.Max(0, ScrollOffset - lines);
        }
        else if (lines < 0)
        {
            ScrollOffset += -lines;
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
        Renderer = new TranscriptRenderer();
        SelectedFoldKey = null;
        RenderedSeq = 0;
        ScrollOffset = 0;
        StickToBottom = true;
        InvalidateWrapCache();
        ReplayEvents();
    }

    /** 切换会话后回放历史事件, 让恢复/重进的会话立刻可见; 序列守卫会跳过与滞留事件的重合部分。 */
    public void ReplayEvents()
    {
        foreach (var sessionEvent in Agent.Session.SnapshotEvents())
            ProcessSessionEvent(sessionEvent, replay: true);
    }

    public void ProcessSessionEvent(SessionEvent sessionEvent, bool replay = false)
    {
        if (sessionEvent.Seq < RenderedSeq)
            return;
        RenderedSeq = sessionEvent.Seq + 1;
        switch (sessionEvent.Data)
        {
            case TurnStartPayload:
                SetBusy(true);
                break;
            case TurnEndPayload:
                SetBusy(false);
                break;
        }

        if (sessionEvent.Data is ToolCallPayload call)
            _pendingSubagentCalls[call.CallId] = (Renderer.CurrentLength, call.Arguments);

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

        var lines = renderer.CompletedLines.Select(line => $"  {line}").ToList();
        if (renderer.Tail.Length > 0)
            lines.Add($"  {renderer.Tail}");
        return string.Join('\n', lines);
    }

    public void AppendRaw(string text)
        => AppendText(text);

    private void AppendText(string text)
    {
        Renderer.AppendSystemMessage(text);
        RequestStickToBottom();
    }

    public void DrawTranscript(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var transcriptVersion = Renderer.Version;
        if (_wrapCacheLines is null || _wrapCacheVersion != transcriptVersion || _wrapCacheWidth != rect.Width)
        {
            RebuildWrapCache(rect.Width);
            _wrapCacheVersion = transcriptVersion;
        }
        var wrapped = _wrapCacheLines!;
        if (wrapped.Count == 0)
            wrapped.Add("");

        var maxOffset = Math.Max(0, wrapped.Count - rect.Height);
        if (StickToBottom)
            ScrollOffset = maxOffset;
        ScrollOffset = Math.Clamp(ScrollOffset, 0, maxOffset);

        var start = ScrollOffset;
        for (var row = 0; row < rect.Height; row++)
        {
            var lineIndex = start + row;
            if (lineIndex >= wrapped.Count)
                break;
            CellText.Draw(grid, rect.X, rect.Y + row, wrapped[lineIndex]);
            ApplyTintHighlight(grid, rect, row, lineIndex);
            ApplySelectionHighlight(grid, rect, row, lineIndex);
        }
    }

    private void DrawMenuOverlay(CellGrid grid, ConsoleRect rect)
    {
        if (CommandMenu?.IsActive == true)
        {
            // 参数阶段画"说明 + 候选"面板; 命令/子命令阶段只在有候选时画列表(无候选不画空浮层)。
            var headers = CommandMenuPanelLines();
            if (headers.Count > 0)
                PopupList.Draw(grid, rect, CommandMenuTitle(), headers, CommandMenu.Candidates, CommandMenu.SelectedIndex);
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

        var lines = RightPanelLines();
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
                    grid[x, y] = new Cell('─', AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
                continue;
            }

            CellText.Draw(grid, rect.X, y, line.Text,
                line.Header ? AnsiColor.BrightCyan : AnsiColor.Default,
                AnsiColor.Default,
                line.Header ? CellStyle.Bold : CellStyle.Dim);
        }
    }

    /** 右栏滚动偏移(自顶部的行数), 供测试断言。 */
    internal int RightPanelScrollOffset => _rightPanelScroll;

    /** 右栏滚轮: 返回是否真的滚动了(供调用方决定要不要重绘)。 */
    public bool ScrollRightPanel(float delta)
    {
        var lines = _rightPanelWheel.Scroll(delta);
        if (lines == 0)
            return false;
        var maximum = Math.Max(0, RightPanelLines().Count - _rightPanelRows);
        var next = Math.Clamp(_rightPanelScroll - lines, 0, maximum);
        if (next == _rightPanelScroll)
            return false;
        _rightPanelScroll = next;
        return true;
    }

    private List<RightPanelLine> RightPanelLines()
    {
        var lines = new List<RightPanelLine>();
        var (provider, model) = CurrentModel(Agent);
        AddPanelSection(lines, "上下文",
        [
            $"session: {Agent.Id}",
            $"title: {Agent.Session.Header.Title ?? "-"}",
            $"cwd: {Agent.Session.Header.Cwd ?? "-"}",
            $"model: {provider}/{model}",
        ]);
        AddPanelSection(lines, "MCP", _window.McpLines());
        AddPanelSection(lines, "计划", ["使用 /plan 管理"]);
        AddPanelSection(lines, "Git 变更", _window.GitLines());
        AddPanelSection(lines, "快捷键",
        [
            "Enter 发送 - / 命令 - @ 引用",
            "Tab 下一个参数/折叠 - Esc 取消",
            "^/v 历史 - PgUp/PgDn 滚动",
            "Ctrl+C×2 退出",
            "Ctrl+X N 新会话 - S 会话",
            "Ctrl+X D detach - K 删会话",
            "Ctrl+X W 总览 - + 分屏 - - 关闭",
            "Ctrl+X 方向键/O 切窗格",
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

    public void DrawInput(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var prompt = PendingApproval is not null ? "approval (y/n/c) " : InputPrompt;
        var caret = Math.Clamp(Cursor, 0, Input.Length);
        var availableWidth = Math.Max(0, rect.Width - prompt.Length);
        var textStart = caret;
        var visibleWidth = 0;
        while (textStart > 0)
        {
            var characterWidth = TerminalTextWidth.Of(Input[textStart - 1]);
            if (visibleWidth + characterWidth > availableWidth)
                break;
            visibleWidth += characterWidth;
            textStart--;
        }

        var visible = Input.AsSpan(textStart);

        // 输入行与说明行锚在输入区底部(上沿往上拖时, 多出来的空行留在上方, 说明行始终紧贴状态行)。
        var inputRow = rect.Height >= 2 ? rect.Bottom - 2 : rect.Y;
        CellText.Draw(grid, rect.X, inputRow, prompt, AnsiColor.Default, AnsiColor.Default, PendingApproval is null ? CellStyle.None : CellStyle.Bold);
        CellText.Draw(grid, rect.X + prompt.Length, inputRow, visible);

        var caretColumn = TerminalTextWidth.Of(Input.AsSpan(textStart, caret - textStart));
        var cursorX = rect.X + Math.Min(prompt.Length + caretColumn, rect.Width - 1);
        var cursorY = inputRow;
        cursorX = Math.Clamp(cursorX, 0, grid.Width - 1);
        cursorY = Math.Clamp(cursorY, 0, grid.Height - 1);
        var cursorCell = grid[cursorX, cursorY];
        grid[cursorX, cursorY] = cursorCell with { Style = cursorCell.Style | CellStyle.Reverse };
        CursorScreenX = cursorX;
        CursorScreenY = cursorY;

        if (rect.Height < 2)
            return;
        var infoY = inputRow + 1;
        if (infoY >= grid.Height)
            return;
        var hint = "Enter 发送 - / 命令 - @ 引用 - Tab 下一个参数 - Ctrl+X 会话";
        CellText.Draw(grid, rect.X, infoY, hint, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
        var (currentProvider, currentModel) = CurrentModel(Agent);
        var preset = CurrentPreset(Agent);
        var profile = CurrentReasoningEffort(Agent) is { } effort
            ? $"{preset} - {currentProvider} - {currentModel} - {effort.Value}"
            : $"{preset} - {currentProvider} - {currentModel}";
        var profileWidth = TerminalTextWidth.Of(profile);
        if (TerminalTextWidth.Of(hint) + profileWidth + 2 < rect.Width)
            CellText.Draw(grid, rect.Right - profileWidth, infoY, profile, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
    }

    public void ShowApprovalPrompt(ApprovalRequest request, TaskCompletionSource<object?> answer)
    {
        PendingApproval = answer;
        var argument = ApprovalHints.PrimaryArgument(request.ToolName, request.Arguments);
        var impact = ApprovalHints.Impact(request.ToolName, request.Arguments);
        var line = $"  ⚠ approve tool \"{request.ToolName}\"?"
            + (request.Reason is null ? "" : $" {request.Reason}")
            + (argument.Length == 0 ? "" : $" - {argument}")
            + (impact.Length == 0 ? "" : $" {impact}")
            + " [y]es/[n]o/[c]ancel turn\n";
        AppendRaw(line);
        StatusText = $"approval pending for \"{request.ToolName}\" - y/n/c";
    }

    private void AnswerApproval(ApprovalOutcome outcome)
    {
        var pending = PendingApproval;
        if (pending is null)
            return;
        PendingApproval = null;
        AppendRaw($"  approval: {outcome}\n");
        SetStatusReady();
        pending.TrySetResult(outcome);
    }

    public void ShowQuestionPrompt(AskUserQuestionRequest request, TaskCompletionSource<object?> answer)
    {
        if (PendingQuestions is not null)
            CompleteQuestions(null);
        PendingQuestions = answer;
        _questionRequest = request;
        _questionIndex = 0;
        _questionAnswers.Clear();
        _questionSelection.Clear();
        _stashedInput = Input;
        _stashedCursor = Cursor;
        Input = "";
        Cursor = 0;
        AppendRaw("  ? 智能体需要你回答:\n");
        ShowCurrentQuestion();
    }

    private void ShowCurrentQuestion()
    {
        var question = _questionRequest!.Questions[_questionIndex];
        AppendRaw($"  ? {question.Question}\n");
        if (question.Detail is { } detail)
            AppendRaw($"    {detail}\n");
        if (question.Options is { Count: > 0 } options)
        {
            for (var index = 0; index < options.Count && index < 9; index++)
            {
                var option = options[index];
                AppendRaw($"    [{index + 1}] {option.Label}{(option.Description is null ? "" : $" - {option.Description}")}\n");
            }
        }
        RefreshQuestionStatus();
    }

    private void RefreshQuestionStatus()
    {
        var question = _questionRequest!.Questions[_questionIndex];
        var total = _questionRequest.Questions.Count;
        var prefix = total > 1 ? $"问题 {_questionIndex + 1}/{total} - " : "";
        StatusText = question.Options is { Count: > 0 }
            ? question.MultiSelect
                ? $"{prefix}1-9 切换选项(已选 {_questionSelection.Count}) - 输入框可补充 - Enter 确认 - Esc 取消"
                : $"{prefix}1-9 选择 - 输入框可补充 - Enter 确认 - Esc 取消"
            : $"{prefix}输入回答 - Enter 确认 - Esc 取消";
    }

    private void HandleQuestionKey(ConsoleKeyInfo key)
    {
        var question = _questionRequest!.Questions[_questionIndex];
        if (key.Key == ConsoleKey.Escape)
        {
            AppendRaw("  x 已取消\n");
            CompleteQuestions(null);
            return;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            SubmitCurrentQuestion(question);
            return;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (Cursor > 0)
            {
                Input = Input.Remove(Cursor - 1, 1);
                Cursor--;
            }
            return;
        }
        if (question.Options is { Count: > 0 } options && key.KeyChar >= '1' && key.KeyChar <= '9')
        {
            var index = key.KeyChar - '1';
            if (index >= options.Count)
                return;
            if (question.MultiSelect)
            {
                if (!_questionSelection.Remove(index))
                    _questionSelection.Add(index);
            }
            else
            {
                _questionSelection.Clear();
                _questionSelection.Add(index);
            }
            RefreshQuestionStatus();
            return;
        }
        if (!char.IsControl(key.KeyChar))
        {
            Input = Input.Insert(Cursor, key.KeyChar.ToString());
            Cursor++;
        }
    }

    private void SubmitCurrentQuestion(AskUserQuestionItem question)
    {
        IReadOnlyList<string> selected = question.Options is { Count: > 0 } options
            ? _questionSelection.Where(index => index < options.Count).Order().Select(index => options[index].Label).ToList()
            : [];
        var custom = Input.Trim();
        if (selected.Count == 0 && custom.Length == 0)
        {
            StatusText = "请先选择选项或输入回答";
            return;
        }
        _questionAnswers.Add(new AskUserQuestionAnswerItem(question.Id, selected, custom.Length > 0 ? custom : null));
        _questionSelection.Clear();
        Input = "";
        Cursor = 0;
        _questionIndex++;
        if (_questionIndex < _questionRequest!.Questions.Count)
        {
            ShowCurrentQuestion();
            return;
        }
        AppendRaw("  + 已回答\n");
        CompleteQuestions(new AskUserQuestionAnswer([.. _questionAnswers]));
    }

    private void CompleteQuestions(object? result)
    {
        var pending = PendingQuestions;
        PendingQuestions = null;
        _questionRequest = null;
        _questionSelection.Clear();
        if (_stashedInput is not null)
        {
            Input = _stashedInput;
            Cursor = _stashedCursor;
            _stashedInput = null;
        }
        SetStatusReady();
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
            if (TryGetMentionToken(text, Cursor, out var start, out var end))
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

    private IReadOnlyList<string> CommandCandidates(
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
                return [.. _window.ProviderCatalogCandidates, .. ProviderTypes.All];

            // 有子命令时按子命令名给候选(如 provider remove), 否则按命令名。
            var descriptor = subcommand ?? command;
            var settings = HarnessSettings.Load(_window.Home);
            return schema?.Name switch
            {
                // provider 的 --type 候选按已注册适配器动态取(插件启用/停用后依然正确)。
                "type" => KnownProviderTypes(),
                "action" when string.Equals(descriptor.Name, "timestamp", StringComparison.OrdinalIgnoreCase) => ["jump", "revert", "fork"],
                "seq" when string.Equals(descriptor.Name, "timestamp", StringComparison.OrdinalIgnoreCase) => _window.TimestampCandidates(),
                _ => descriptor.Name switch
            {
                "model" => settings.Providers
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .SelectMany(entry => entry.Value.Models.Keys
                        .OrderBy(model => model, StringComparer.Ordinal)
                        .Select(model => $"{entry.Key}/{model}"))
                    .ToList(),
                "remove" => settings.Providers.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList(),
                "session" => _window.CurrentSessions().Select(session => session.Id).ToList(),
                "skill" => _window.SkillCandidates,
                "reasoning" => ReasoningEffortCandidates(),
                "gpu" => GpuCatalog.SelectionLabels(),
                _ => [],
                },
            };
        }
        catch (Exception)
        {
            return [];
        }
    }

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

    /** 会话 preset 持久在 `preset/mode` 事件里; Dsh.Tui 不引用 Dsh.Presets, 属性名按约定泛化读取。 */
    private static string CurrentPreset(IAgent agent)
    {
        var preset = "standard";
        foreach (var sessionEvent in agent.Session.SnapshotEvents())
        {
            if (sessionEvent.Type != "preset/mode")
                continue;
            preset = ReadStringProperty(sessionEvent.Data, "Preset") ?? preset;
        }
        return preset;
    }

    private static string? ReadStringProperty(SessionEventPayload payload, string name)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), DshJson.Options);
            if (node is not JsonObject json)
                return null;
            foreach (var (key, value) in json)
            {
                if (value is not null && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    return value.GetValue<string>();
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

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
        var (inserted, cursor) = ReplaceMention(Input, _mentionStart, _mentionEnd, _mentionCandidates[_mentionIndex]);
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

    public void SetStatusReady()
        => StatusText = Busy
            ? "working~ (Esc to cancel)"
            : ReadyStatus;

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
            : MentionResolver.ExpandMentions(text, CurrentCwd(), _window.CurrentSessions());
        var content = new List<ContentBlock>();
        if (expandedText.Length > 0)
            content.Add(new TextBlock(expandedText));
        foreach (var attachment in _attachments)
            content.Add(new ImageBlock(attachment));
        _attachments.Clear();
        SetBusy(true);
        Agent.Followup(MessageFactory.CreateUserMessage(content));
    }

    /** 仅当光标落在「@token」区间内部或紧邻末尾时才算活跃 mention, 否则(如 token 后又输入了提示词)视为普通文本。 */
    internal static bool TryGetMentionToken(string text, int cursor, out int start, out int end)
    {
        var at = text.LastIndexOf('@');
        if (at < 0)
        {
            start = 0;
            end = 0;
            return false;
        }

        var tokenEnd = at + 1;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]) && text[tokenEnd] != '@')
            tokenEnd++;
        if (cursor <= at || cursor > tokenEnd)
        {
            start = 0;
            end = 0;
            return false;
        }

        start = at;
        end = tokenEnd;
        return true;
    }

    /** 只替换 mention token 区间并补尾随空格, 保留 token 之后已输入的文本; 返回新文本与光标位置。 */
    internal static (string Text, int Cursor) ReplaceMention(string text, int start, int end, string candidate)
        => ($"{text[..start]}@{candidate} {text[end..]}", start + candidate.Length + 2);

    private void RecallHistory(int direction)
    {
        if (_history.Count == 0)
            return;
        _historyIndex = _historyIndex < 0
            ? direction < 0 ? _history.Count - 1 : -1
            : Math.Clamp(_historyIndex + direction, -1, _history.Count - 1);
        Input = _historyIndex < 0 ? "" : _history[_historyIndex];
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
        _wrapCacheLines = null;
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
        var lines = _wrapCacheLines;
        if (lines is null || lines.Count == 0)
            return null;
        var (firstRow, firstColumn, lastRow, lastColumn) = OrderedSelection();
        firstRow = Math.Max(0, firstRow);
        lastRow = Math.Min(lines.Count - 1, lastRow);
        if (lastRow < firstRow)
            return null;
        var builder = new StringBuilder();
        for (var row = firstRow; row <= lastRow; row++)
        {
            var line = lines[row];
            var start = row == firstRow ? Math.Min(firstColumn, line.Length) : 0;
            var end = row == lastRow ? Math.Min(lastColumn + 1, line.Length) : line.Length;
            if (end > start)
                builder.Append(line, start, end - start);
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
        var lineIndex = LineIndexOf(Renderer.LineStarts, Renderer.CompletedLines.Count, Renderer.TailStart, offset.Value);
        var tints = Renderer.LineTints;
        if (lineIndex < 0 || lineIndex >= tints.Count)
            return;
        var (foreground, background, style) = tints[lineIndex] switch
        {
            TranscriptTint.DiffAdded => (AnsiColor.Black, AnsiColor.Green, CellStyle.None),
            TranscriptTint.DiffRemoved => (AnsiColor.BrightWhite, AnsiColor.Red, CellStyle.None),
            TranscriptTint.DiffTitle => (AnsiColor.BrightCyan, AnsiColor.Default, CellStyle.Bold),
            _ => (AnsiColor.Default, AnsiColor.Default, CellStyle.None),
        };
        if (foreground == AnsiColor.Default && background == AnsiColor.Default && style == CellStyle.None)
            return;
        for (var column = 0; column < rect.Width; column++)
        {
            var x = rect.X + column;
            if (x < 0 || x >= grid.Width)
                continue;
            var cell = grid[x, rect.Y + row];
            grid[x, rect.Y + row] = new Cell(cell.Character, foreground, background, style);
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

    /** 命中 fold 头行(折叠态=预览行, 展开态=首行)时切换折叠; 命中返回 true。 */
    public bool TryToggleFoldAt(int cellY, ConsoleRect rect)
    {
        var wrappedIndex = ScrollOffset + (cellY - rect.Y);
        var offset = SourceOffsetAt(wrappedIndex);
        if (offset is null)
            return false;
        var fold = Renderer.Folds.FirstOrDefault(candidate => candidate.Start == offset.Value && candidate.End > candidate.Start);
        if (fold is null)
            return false;
        ToggleFold(fold);
        return true;
    }

    /** 键盘(Enter)与鼠标点击共用同一切换入口。 */
    public void ToggleFold(TranscriptFold fold)
    {
        fold.Collapsed = !fold.Collapsed;
        Renderer.BumpVersion();
    }

    /** 视觉行 > 源偏移: rows 的 FlatStart 升序, 二分找最后一个不超过 wrappedIndex 的行。 */
    private int? SourceOffsetAt(int wrappedIndex)
    {
        var rows = _visualRows;
        if (_wrapCacheLines is null || rows.Count == 0 || wrappedIndex < 0 || wrappedIndex >= _wrapCacheLines.Count)
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

    private sealed class VisualRow
    {
        public required int SourceStart { get; init; }
        public required int FlatStart { get; init; }
    }

    private void RebuildWrapCache(int width)
    {
        var lines = Renderer.CompletedLines;
        var starts = Renderer.LineStarts;
        var tail = Renderer.Tail;
        var tailStart = Renderer.TailStart;
        var folds = Renderer.Folds;
        var from = 0;
        if (_wrapCacheLines is not null && _wrapCacheWidth == width && _wrapProcessedLine <= lines.Count + 1)
        {
            from = _wrapProcessedLine;
            for (var index = 0; index < folds.Count; index++)
            {
                var fold = folds[index];
                if (index < _foldSnapshot.Count && _foldSnapshot[index] == (fold.Start, fold.End, fold.Collapsed))
                    continue;
                if (index >= _foldSnapshot.Count && !fold.Collapsed)
                    continue;
                from = Math.Min(from, LineIndexOf(starts, lines.Count, tailStart, fold.Start));
            }
        }
        RebuildWrapFromLines(lines, starts, tail, tailStart, folds, width, from);
        SnapshotFolds(folds);
        _wrapCacheWidth = width;
    }

    /** 行索引空间的增量重排: 全文不再物化, 行文本直接取 renderer 的行列表; fold 偏移经 LineStarts 二分映射为行号。 */
    private void RebuildWrapFromLines(IReadOnlyList<string> lines, IReadOnlyList<int> starts, string tail, int tailStart, IReadOnlyList<TranscriptFold> folds, int width, int fromLine)
    {
        var rows = _visualRows;
        var wrapped = _wrapCacheLines ??= [];
        var fromOffset = fromLine < lines.Count ? starts[fromLine] : tailStart;
        var rowIndex = LowerBoundRow(rows, fromOffset);
        var flatIndex = rowIndex < rows.Count ? rows[rowIndex].FlatStart : wrapped.Count;
        if (rowIndex < rows.Count)
            rows.RemoveRange(rowIndex, rows.Count - rowIndex);
        wrapped.RemoveRange(flatIndex, wrapped.Count - flatIndex);

        var lineIndex = fromLine;
        while (lineIndex <= lines.Count)
        {
            var lineStart = lineIndex < lines.Count ? starts[lineIndex] : tailStart;
            var raw = lineIndex < lines.Count ? lines[lineIndex] : tail;
            var fold = FindCollapsedFold(folds, lineStart);
            if (fold is not null)
            {
                AddRow(lineStart, fold.Preview, width);
                var endLine = LineIndexOf(starts, lines.Count, tailStart, fold.End);
                var endLineStart = endLine < lines.Count ? starts[endLine] : tailStart;
                if (fold.End <= endLineStart)
                {
                    lineIndex = endLine;
                }
                else
                {
                    var endRaw = endLine < lines.Count ? lines[endLine] : tail;
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
        _wrapProcessedLine = lines.Count;
    }

    /** offset > 行号: 行 i 覆盖 [starts[i], 下一行起点), tail 行覆盖 [tailStart, 末尾)。 */
    private static int LineIndexOf(IReadOnlyList<int> starts, int completeCount, int tailStart, int offset)
    {
        if (offset >= tailStart)
            return completeCount;
        var low = 0;
        var high = completeCount;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (starts[mid] <= offset)
                low = mid + 1;
            else
                high = mid;
        }
        return Math.Max(0, low - 1);
    }

    private static TranscriptFold? FindCollapsedFold(IReadOnlyList<TranscriptFold> folds, int position)
    {
        TranscriptFold? outermost = null;
        foreach (var fold in folds)
        {
            if (!fold.Collapsed || fold.Start > position || position >= fold.End)
                continue;
            if (outermost is null || fold.Start < outermost.Start)
                outermost = fold;
        }
        return outermost;
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

    private void AddRow(int sourceStart, string content, int width)
    {
        var probe = new List<string>();
        WrapSingleLine(content, width, probe);
        _visualRows.Add(new VisualRow { SourceStart = sourceStart, FlatStart = _wrapCacheLines!.Count });
        _wrapCacheLines.AddRange(probe);
    }

    private void SnapshotFolds(IReadOnlyList<TranscriptFold> folds)
    {
        _foldSnapshot.Clear();
        foreach (var fold in folds)
            _foldSnapshot.Add((fold.Start, fold.End, fold.Collapsed));
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
        PendingQuestions?.TrySetResult(null);
    }
}
