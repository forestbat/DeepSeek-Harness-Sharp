using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Pty;

namespace Dsh.Tui;

public sealed class ChatWindow : IDisposable
{
    private readonly Context _ctx;
    private readonly HarnessHome _home;
    private readonly object _gate = new();
    private readonly Queue<SessionEvent> _pendingEvents = [];
    private readonly Queue<Action> _pendingActions = [];
    private readonly List<string> _history = [];
    private readonly MentionResolver _mentionResolver = new();
    private readonly ISessionPersistence? _persistence;
    private IReadOnlyList<SessionInfo>? _sessionInfos;
    private IReadOnlyList<string> _skillCandidates = [];
    private string? _selectedFoldKey;
    private AgentLoopAgent _agent;
    private TranscriptRenderer _renderer = new();
    private Func<bool> _unsubscribe;
    private readonly Func<bool> _approvalSubscription;
    private readonly Func<bool> _questionsSubscription;
    private readonly Func<bool> _skillChangeSubscription;
    private TaskCompletionSource<object?>? _pendingApproval;
    private TaskCompletionSource<object?>? _pendingQuestions;
    private AskUserQuestionRequest? _questionRequest;
    private readonly List<AskUserQuestionAnswerItem> _questionAnswers = [];
    private readonly HashSet<int> _questionSelection = [];
    private int _questionIndex;
    private string? _stashedInput;
    private int _stashedCursor;
    private CommandMenuState? _commandMenu;
    private IReadOnlyList<string> _mentionCandidates = [];
    private int _mentionIndex;
    private int _mentionStart;
    private bool _mentionActive;
    private int _historyIndex = -1;
    private bool _busy;
    private long _renderedSeq;
    private string _input = "";
    private int _cursor;
    private int _scrollOffset;
    private volatile bool _stickToBottom = true;
    private IReadOnlyList<string>? _mcpPanelLines;
    private string _statusText = "ready — Enter to send, ↑ history, Esc cancels a running turn, Ctrl+C×2 quits";
    private const long ExitConfirmWindowMs = 2000;

    private bool _exitRequested;
    private long? _exitConfirmAt;
    private bool _ctrlXPrefix;
    private string? _deleteConfirmSessionId;
    private int _wrapCacheVersion = -1;
    private int _wrapCacheWidth = -1;
    private List<string>? _wrapCacheLines;
    private int _wrapProcessedLine;
    private bool _sessionRenamedSubscribed;

    public ChatWindow(Context ctx, AgentLoopAgent agent, HarnessHome home, ISessionPersistence? persistence = null)
    {
        _ctx = ctx;
        _agent = agent;
        _home = home;
        _persistence = persistence;
        SubscribeSessionRenamed();
        LoadSkillCandidates();

        _unsubscribe = ctx.On<SessionEventNotification>(notification =>
        {
            if (!ReferenceEquals(notification.Session, _agent.Session))
                return;
            QueueSessionEvent(notification.Event);
        });

        _approvalSubscription = ctx.OnWaterfall<ApprovalRequestNotification>((notification, _) =>
        {
            var request = notification.Request;
            var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueAction(() => ShowApprovalPrompt(request, answer));
            return new ValueTask<object?>(answer.Task);
        }, new EventOptions { Global = true });

        _questionsSubscription = ctx.OnWaterfall<UserQuestionsRequestNotification>((notification, _) =>
        {
            var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueAction(() => ShowQuestionPrompt(notification.Request, answer));
            return new ValueTask<object?>(answer.Task);
        }, new EventOptions { Global = true });

        _skillChangeSubscription = ctx.On<SkillsChangedNotification>(_ =>
        {
            LoadSkillCandidates();
        }, new EventOptions { Global = true });

        ReplayEvents();
    }

    public bool ExitRequested => _exitRequested;

    public int RenderVersion { get; private set; }

    public int CursorScreenX { get; private set; }

    public int CursorScreenY { get; private set; }

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

        List<SessionEvent> sessionEvents = [];
        lock (_gate)
        {
            while (_pendingEvents.Count > 0)
                sessionEvents.Add(_pendingEvents.Dequeue());
        }

        foreach (var sessionEvent in sessionEvents)
            ProcessSessionEvent(sessionEvent);

        if (actions.Count > 0 || sessionEvents.Count > 0)
            RenderVersion++;
    }

    public void HandleKey(ConsoleKeyInfo key)
    {
        RenderVersion++;
        if (_pendingApproval is not null)
        {
            if (key.Key == ConsoleKey.Y)
                AnswerApproval(ApprovalOutcome.AllowedOnce);
            else if (key.Key == ConsoleKey.N)
                AnswerApproval(ApprovalOutcome.Rejected);
            else if (key.Key == ConsoleKey.C || key.Key == ConsoleKey.Escape)
                AnswerApproval(ApprovalOutcome.Cancelled);
            return;
        }

        if (_pendingQuestions is not null)
        {
            HandleQuestionKey(key);
            return;
        }

        if (_ctrlXPrefix)
        {
            _ctrlXPrefix = false;
            RefreshMenuStatus();
            switch (key.Key)
            {
                case ConsoleKey.N:
                    RunSlashCommand("/new");
                    return;
                case ConsoleKey.S:
                    _input = "/session ";
                    _cursor = _input.Length;
                    RefreshMenus();
                    return;
                case ConsoleKey.D:
                    RunSlashCommand("/detach");
                    return;
                case ConsoleKey.K:
                    _input = "/session delete ";
                    _cursor = _input.Length;
                    RefreshMenus();
                    return;
            }
        }

        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            switch (key.Key)
            {
                case ConsoleKey.X:
                    _ctrlXPrefix = true;
                    _statusText = "Ctrl+X: N 新会话 · S 会话 · D detach · K 删会话";
                    return;
                case ConsoleKey.C:
                    if (_busy)
                    {
                        ClearExitConfirm();
                        _agent.Cancel(new AgentCancelCause.User());
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
                    _statusText = "再按一次 Ctrl+C 退出";
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

        if (_selectedFoldKey is not null)
        {
            if (key.Key == ConsoleKey.Escape)
            {
                _selectedFoldKey = null;
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
                    selected.Collapsed = !selected.Collapsed;
                    _renderer.BumpVersion();
                    _selectedFoldKey = null;
                    return;
                }
            }
        }

        if (key.Key == ConsoleKey.Tab && _commandMenu?.IsActive != true && !_mentionActive)
        {
            if (_selectedFoldKey is null)
            {
                var first = _renderer.Folds.FirstOrDefault();
                if (first is not null)
                    _selectedFoldKey = FoldKey(first);
            }
            else
            {
                SelectNextFold(1);
            }
            return;
        }

        if (_commandMenu?.IsActive == true || _mentionActive)
        {
            if (key.Key == ConsoleKey.Delete
                && _commandMenu?.IsActive == true
                && _commandMenu.Stage == CommandMenuState.MenuStage.Argument
                && string.Equals(_commandMenu.CurrentCommand?.Name, "session", StringComparison.OrdinalIgnoreCase))
            {
                if (_deleteConfirmSessionId is null)
                {
                    var candidate = _commandMenu.Candidates.ElementAtOrDefault(_commandMenu.SelectedIndex);
                    if (candidate is not null)
                    {
                        _deleteConfirmSessionId = candidate;
                        AppendRaw($"  press Delete again to delete session {candidate}\n");
                    }
                }
                else
                {
                    var id = _deleteConfirmSessionId;
                    _deleteConfirmSessionId = null;
                    RunSlashCommand($"/session delete {id}");
                }
                return;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                _deleteConfirmSessionId = null;
                if (_commandMenu?.IsActive == true)
                {
                    _commandMenu.Back();
                    SyncCommandMenuInput();
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

            if (key.Key == ConsoleKey.Enter)
            {
                if (_commandMenu?.IsActive == true)
                {
                    _commandMenu.Close();
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

            if (key.Key == ConsoleKey.Tab && _commandMenu?.IsActive == true)
            {
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
                if (_busy)
                    _agent.Cancel(new AgentCancelCause.User());
                break;
            case ConsoleKey.UpArrow:
                RecallHistory(-1);
                break;
            case ConsoleKey.DownArrow:
                RecallHistory(1);
                break;
            case ConsoleKey.LeftArrow:
                _cursor = Math.Max(0, _cursor - 1);
                break;
            case ConsoleKey.RightArrow:
                _cursor = Math.Min(_input.Length, _cursor + 1);
                break;
            case ConsoleKey.Home:
                _cursor = 0;
                break;
            case ConsoleKey.End:
                _cursor = _input.Length;
                break;
            case ConsoleKey.Backspace:
                if (_cursor > 0)
                {
                    _input = _input.Remove(_cursor - 1, 1);
                    _cursor--;
                    RefreshMenus();
                }

                break;
            case ConsoleKey.Delete:
                if (_cursor < _input.Length)
                {
                    _input = _input.Remove(_cursor, 1);
                    RefreshMenus();
                }

                break;
            case ConsoleKey.PageUp:
                _stickToBottom = false;
                _scrollOffset = Math.Max(0, _scrollOffset - 10);
                break;
            case ConsoleKey.PageDown:
                _scrollOffset += 10;
                break;
            default:
                if (key.KeyChar >= ' ')
                {
                    _input = _input.Insert(_cursor, key.KeyChar.ToString());
                    _cursor++;
                    RefreshMenus();
                }

                break;
        }
    }

    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text) || _pendingApproval is not null)
            return;
        var sanitized = text.Replace('\r', ' ').Replace('\n', ' ');
        if (sanitized.Length == 0)
            return;
        _input = _input.Insert(_cursor, sanitized);
        _cursor += sanitized.Length;
        RefreshMenus();
    }

    public void HandleMouseWheel(int delta)
    {
        RenderVersion++;
        if (_pendingApproval is not null)
            return;
        if (delta > 0)
        {
            _stickToBottom = false;
            _scrollOffset = Math.Max(0, _scrollOffset - 10);
        }
        else if (delta < 0)
        {
            _scrollOffset += 10;
        }
    }

    public void HandleMouseClick(int cellX, int cellY, UiLayout layout)
    {
        RenderVersion++;
        if (_pendingApproval is not null)
            return;
        if (layout.Input.Contains(cellX, cellY))
        {
            _cursor = ColumnToCharIndex(Math.Clamp(cellX - layout.Input.X - InputPrompt.Length, 0, TerminalTextWidth.Of(_input)));
            RefreshMenus();
            return;
        }

        if (layout.Main.Contains(cellX, cellY))
            _stickToBottom = false;
    }

    public void Draw(CellGrid grid, UiLayout layout)
    {
        grid.Clear();
        DrawMain(grid, layout.Main);
        DrawRightPanel(grid, layout.RightPanel);
        DrawInput(grid, layout.Input);
        DrawStatus(grid, layout.Status);
        DrawDividers(grid, layout);
    }

    public void RequestExit()
        => _exitRequested = true;

    private void ClearExitConfirm()
    {
        if (_exitConfirmAt is null)
            return;
        _exitConfirmAt = null;
        RefreshMenuStatus();
    }

    public void Dispose()
    {
        UnsubscribeSessionRenamed();
        _unsubscribe.Invoke();
        _approvalSubscription.Invoke();
        _questionsSubscription.Invoke();
        _skillChangeSubscription.Invoke();
        _pendingApproval?.TrySetResult(ApprovalOutcome.Cancelled);
        _pendingQuestions?.TrySetResult(null);
    }

    private void QueueSessionEvent(SessionEvent sessionEvent)
    {
        lock (_gate)
            _pendingEvents.Enqueue(sessionEvent);
    }

    private void QueueAction(Action action)
    {
        lock (_gate)
            _pendingActions.Enqueue(action);
    }

    private void ClearPendingEvents()
    {
        lock (_gate)
        {
            _pendingEvents.Clear();
            _pendingActions.Clear();
        }
    }

    private void ProcessSessionEvent(SessionEvent sessionEvent, bool replay = false)
    {
        if (sessionEvent.Seq < _renderedSeq)
            return;
        _renderedSeq = sessionEvent.Seq + 1;
        switch (sessionEvent.Data)
        {
            case TurnStartPayload:
                SetBusy(true);
                break;
            case TurnEndPayload:
                SetBusy(false);
                break;
        }

        _renderer.AppendSessionEvent(sessionEvent, replay);
    }

    private void AppendRaw(string text)
    {
        AppendText(text);
    }

    private void AppendText(string text)
    {
        _renderer.AppendSystemMessage(text);
        _stickToBottom = true;
    }

    private void SwitchAgent(AgentLoopAgent agent)
    {
        UnsubscribeSessionRenamed();
        _unsubscribe.Invoke();
        ClearPendingEvents();
        _agent = agent;
        SubscribeSessionRenamed();
        _renderer = new TranscriptRenderer();
        _selectedFoldKey = null;
        _renderedSeq = 0;
        _scrollOffset = 0;
        _stickToBottom = true;
        InvalidateWrapCache();
        _unsubscribe = _ctx.On<SessionEventNotification>(notification =>
        {
            if (!ReferenceEquals(notification.Session, _agent.Session))
                return;
            QueueSessionEvent(notification.Event);
        });
        ReplayEvents();
    }

    private void InvalidateWrapCache()
    {
        _wrapCacheVersion = -1;
        _wrapCacheWidth = -1;
        _wrapCacheLines = null;
        _wrapProcessedLine = 0;
        _visualRows.Clear();
        _foldSnapshot.Clear();
    }

    /** 切换会话后回放历史事件, 让恢复/重进的会话立刻可见; 序列守卫会跳过与滞留事件的重合部分。 */
    private void ReplayEvents()
    {
        foreach (var sessionEvent in _agent.Session.SnapshotEvents())
            ProcessSessionEvent(sessionEvent, replay: true);
    }

    private void SubscribeSessionRenamed()
    {
        if (_sessionRenamedSubscribed)
            return;
        _agent.Session.Renamed += HandleSessionRenamed;
        _sessionRenamedSubscribed = true;
    }

    private void UnsubscribeSessionRenamed()
    {
        if (!_sessionRenamedSubscribed)
            return;
        _agent.Session.Renamed -= HandleSessionRenamed;
        _sessionRenamedSubscribed = false;
    }

    private void HandleSessionRenamed(Session session, SessionHeader header)
    {
        QueueAction(() => _sessionInfos = null);
    }

    private void ShowApprovalPrompt(ApprovalRequest request, TaskCompletionSource<object?> answer)
    {
        _pendingApproval = answer;
        var argument = ApprovalHints.PrimaryArgument(request.ToolName, request.Arguments);
        var impact = ApprovalHints.Impact(request.ToolName, request.Arguments);
        var line = $"  ⚠ approve tool \"{request.ToolName}\"?"
            + (request.Reason is null ? "" : $" {request.Reason}")
            + (argument.Length == 0 ? "" : $" · {argument}")
            + (impact.Length == 0 ? "" : $" {impact}")
            + " [y]es/[n]o/[c]ancel turn\n";
        AppendRaw(line);
        _statusText = $"approval pending for \"{request.ToolName}\" — y/n/c";
    }

    private void AnswerApproval(ApprovalOutcome outcome)
    {
        var pending = _pendingApproval;
        if (pending is null)
            return;
        _pendingApproval = null;
        AppendRaw($"  approval: {outcome}\n");
        SetStatusReady();
        pending.TrySetResult(outcome);
    }

    private void ShowQuestionPrompt(AskUserQuestionRequest request, TaskCompletionSource<object?> answer)
    {
        if (_pendingQuestions is not null)
            CompleteQuestions(null);
        _pendingQuestions = answer;
        _questionRequest = request;
        _questionIndex = 0;
        _questionAnswers.Clear();
        _questionSelection.Clear();
        _stashedInput = _input;
        _stashedCursor = _cursor;
        _input = "";
        _cursor = 0;
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
                AppendRaw($"    [{index + 1}] {option.Label}{(option.Description is null ? "" : $" — {option.Description}")}\n");
            }
        }
        RefreshQuestionStatus();
    }

    private void RefreshQuestionStatus()
    {
        var question = _questionRequest!.Questions[_questionIndex];
        var total = _questionRequest.Questions.Count;
        var prefix = total > 1 ? $"问题 {_questionIndex + 1}/{total} · " : "";
        _statusText = question.Options is { Count: > 0 }
            ? question.MultiSelect
                ? $"{prefix}1-9 切换选项(已选 {_questionSelection.Count}) · 输入框可补充 · Enter 确认 · Esc 取消"
                : $"{prefix}1-9 选择 · 输入框可补充 · Enter 确认 · Esc 取消"
            : $"{prefix}输入回答 · Enter 确认 · Esc 取消";
    }

    private void HandleQuestionKey(ConsoleKeyInfo key)
    {
        var question = _questionRequest!.Questions[_questionIndex];
        if (key.Key == ConsoleKey.Escape)
        {
            AppendRaw("  ✗ 已取消\n");
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
            if (_cursor > 0)
            {
                _input = _input.Remove(_cursor - 1, 1);
                _cursor--;
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
            _input = _input.Insert(_cursor, key.KeyChar.ToString());
            _cursor++;
        }
    }

    private void SubmitCurrentQuestion(AskUserQuestionItem question)
    {
        IReadOnlyList<string> selected = question.Options is { Count: > 0 } options
            ? _questionSelection.Where(index => index < options.Count).Order().Select(index => options[index].Label).ToList()
            : [];
        var custom = _input.Trim();
        if (selected.Count == 0 && custom.Length == 0)
        {
            _statusText = "请先选择选项或输入回答";
            return;
        }
        _questionAnswers.Add(new AskUserQuestionAnswerItem(question.Id, selected, custom.Length > 0 ? custom : null));
        _questionSelection.Clear();
        _input = "";
        _cursor = 0;
        _questionIndex++;
        if (_questionIndex < _questionRequest!.Questions.Count)
        {
            ShowCurrentQuestion();
            return;
        }
        AppendRaw("  ✓ 已回答\n");
        CompleteQuestions(new AskUserQuestionAnswer([.. _questionAnswers]));
    }

    private void CompleteQuestions(object? result)
    {
        var pending = _pendingQuestions;
        _pendingQuestions = null;
        _questionRequest = null;
        _questionSelection.Clear();
        if (_stashedInput is not null)
        {
            _input = _stashedInput;
            _cursor = _stashedCursor;
            _stashedInput = null;
        }
        SetStatusReady();
        pending?.TrySetResult(result);
    }

    private void RefreshMenus()
    {
        var text = _input;
        if (text.StartsWith('/'))
        {
            if (_commandMenu is not { IsActive: true })
                _commandMenu = CreateCommandMenu();
            _commandMenu.ApplyInput(text);
            CloseMentionMenu();
        }
        else
        {
            if (_commandMenu?.IsActive == true)
                _commandMenu.Close();
            _deleteConfirmSessionId = null;
            if (TryGetMentionToken(text, out var start, out var token))
            {
                _mentionActive = true;
                _mentionStart = start;
                _mentionCandidates = _mentionResolver.ResolveCandidates(token, CurrentCwd(), CurrentSessions());
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
        var commands = _ctx.Get<CommandsService>(CommandsService.ServiceName)?.List(_agent) ?? [];
        if (commands.All(command => !string.Equals(command.Name, "exit", StringComparison.OrdinalIgnoreCase)))
            commands = [.. commands, new CommandDescriptor("exit", "退出 TUI")];
        var descriptors = CommandMenuCatalog.Enrich(commands);
        return new CommandMenuState(descriptors, CommandCandidates);
    }

    private IReadOnlyList<string> CommandCandidates(CommandDescriptor descriptor)
    {
        try
        {
            var settings = HarnessSettings.Load(_home);
            return descriptor.Name switch
            {
                "model" => settings.Providers
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .SelectMany(entry => entry.Value.Models.Keys
                        .OrderBy(model => model, StringComparer.Ordinal)
                        .Select(model => $"{entry.Key}/{model}"))
                    .ToList(),
                "remove" => settings.Providers.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList(),
                "session" => CurrentSessions().Select(session => session.Id).ToList(),
                "skill" => _skillCandidates,
                "reasoning" => ReasoningEffortCandidates(),
                _ => [],
            };
        }
        catch (Exception)
        {
            return [];
        }
    }

    private IReadOnlyList<string> ReasoningEffortCandidates()
    {
        var (provider, model) = CurrentModel(_agent);
        if (provider is null || model is null)
            return [];
        var info = _ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false)?.ResolveModelInfo(provider, model);
        return info?.Reasoning?.Efforts.Select(effort => effort.Id.Value).ToList() ?? [];
    }

    private static (string? Provider, string? Model) CurrentModel(IAgent agent)
    {
        var config = agent.Session.RequestHeader()?.Config;
        return (config?.Provider ?? agent.Options.Provider, config?.Model ?? agent.Options.Model);
    }

    private void MoveMenuSelection(int direction)
    {
        if (_commandMenu?.IsActive == true)
        {
            if (direction < 0)
                _commandMenu.MoveUp();
            else
                _commandMenu.MoveDown();
        }
        else if (_mentionActive && _mentionCandidates.Count > 0)
        {
            _mentionIndex = Math.Clamp(_mentionIndex + direction, 0, _mentionCandidates.Count - 1);
        }

        RefreshMenuStatus();
    }

    private void ConfirmCommandMenu()
    {
        var completed = _commandMenu!.Confirm();
        if (completed is not null)
        {
            _commandMenu.Close();
            _input = completed;
            _cursor = _input.Length;
            RefreshMenuStatus();
            Submit();
            return;
        }

        SyncCommandMenuInput();
        RefreshMenuStatus();
    }

    private void SyncCommandMenuInput()
    {
        if (_commandMenu?.IsActive == true)
        {
            _input = _commandMenu.Prefix + _commandMenu.Query;
            _cursor = _input.Length;
        }
    }

    private void InsertMentionCandidate()
    {
        var text = _input;
        var candidate = _mentionCandidates[_mentionIndex];
        _input = $"{text[.._mentionStart]}@{candidate}";
        _cursor = _input.Length;
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

    private void RefreshMenuStatus()
    {
        if (_commandMenu?.IsActive == true)
        {
            _statusText = $"{_commandMenu.Prompt} — ↑/↓ 移动 · Tab 选择 · Enter 发送 · Esc 返回";
            return;
        }

        if (_mentionActive)
        {
            _statusText = $"@ {_mentionCandidates.Count} candidates — ↑/↓ Enter Esc";
            return;
        }

        SetStatusReady();
    }

    private void SetStatusReady()
        => _statusText = _busy
            ? "working… (Esc to cancel)"
            : "ready — Enter to send, ↑ history, Esc cancels a running turn, Ctrl+C×2 quits";

    private string CurrentCwd()
        => _agent.Session.Header.Cwd ?? Environment.CurrentDirectory;

    private IReadOnlyList<SessionInfo> CurrentSessions()
    {
        if (_sessionInfos is not null)
            return _sessionInfos;

        var result = new List<SessionInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_persistence is not null)
        {
            try
            {
                foreach (var snapshot in _persistence.List())
                {
                    var info = SessionInfo.FromSnapshot(snapshot);
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
            var info = new SessionInfo(agent.Id.ToString(), agent.Session.Header.Title, null);
            if (seen.Add(info.Id))
                result.Add(info);
        }

        _sessionInfos = result;
        return result;
    }

    private void LoadSkillCandidates()
    {
        var catalog = _ctx.Get<ISkillCatalog>(ISkillCatalog.ServiceName, false);
        if (catalog is null)
            return;
        _ = LoadSkillCandidatesAsync(catalog);
    }

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

    private static bool TryGetMentionToken(string text, out int start, out string token)
    {
        var at = text.LastIndexOf('@');
        if (at < 0)
        {
            start = 0;
            token = "";
            return false;
        }

        var end = at + 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '@')
            end++;
        start = at;
        token = text[(at + 1)..end];
        return true;
    }

    private void RecallHistory(int direction)
    {
        if (_history.Count == 0)
            return;
        _historyIndex = _historyIndex < 0
            ? direction < 0 ? _history.Count - 1 : -1
            : Math.Clamp(_historyIndex + direction, -1, _history.Count - 1);
        _input = _historyIndex < 0 ? "" : _history[_historyIndex];
        _cursor = _input.Length;
    }

    private void Submit()
    {
        var text = _input.Trim();
        if (text.Length == 0)
            return;
        _input = "";
        _cursor = 0;
        _history.Add(text);
        _historyIndex = -1;

        var displayMessage = MessageFactory.CreateUserText(text);
        _renderer.AppendUserMessage(displayMessage);
        _stickToBottom = true;

        if (text.StartsWith('/'))
        {
            RunSlashCommand(text);
            return;
        }

        var expandedText = _mentionResolver.ExpandMentions(text, CurrentCwd(), CurrentSessions());
        SetBusy(true);
        _agent.Followup(MessageFactory.CreateUserText(expandedText));
    }

    private async void RunSlashCommand(string text)
    {
        switch (text.Split(' ', 2)[0])
        {
            case "/quit" or "/exit":
                RequestExit();
                break;
            case "/new":
                await NewSession(text);
                break;
            case "/resume":
                await ResumeSession(text);
                break;
            case "/session":
                await ListSessions(text);
                break;
            case "/detach":
                await DetachSession(text);
                break;
            case "/gpu":
                SelectGpu(text);
                break;
            default:
                var commands = _ctx.Get<CommandsService>(CommandsService.ServiceName);
                if (commands is null)
                {
                    AppendRaw($"  unknown command: {text} (available: /quit, /exit)\n");
                    break;
                }

                try
                {
                    var execution = await commands.Execute(_agent, text);
                    string? resultText = null;
                    if (execution is null)
                        resultText = $"  unknown command: {text}\n";
                    else if (execution.Result is CommandResult.Success { Text: { } successText } && successText.Length > 0)
                        resultText = $"  {successText}\n";
                    else if (execution.Result is CommandResult.Error error)
                        resultText = $"  {error.Text}\n";

                    if (resultText is not null)
                    {
                        var captured = resultText;
                        QueueAction(() => AppendRaw(captured));
                    }
                }
                catch (Exception error)
                {
                    var message = error.Message;
                    QueueAction(() => AppendRaw($"  command failed: {message}\n"));
                }

                break;
        }
    }

    private async Task NewSession(string text)
    {
        var cwd = text["/new".Length..].Trim();
        if (cwd.Length == 0)
            cwd = Environment.CurrentDirectory;
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions(_agent.Options.Provider, _agent.Options.Model, _agent.Options.ReasoningEffort, _agent.Options.MaxTokens)));
        var newAgent = (AgentLoopAgent)handle.Agent;
        await newAgent.WhenIdle();
        QueueAction(() =>
        {
            SwitchAgent(newAgent);
            AppendRaw($"  new session: {newAgent.Id}\n");
        });
    }

    private async Task ResumeSession(string text)
    {
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var raw = text["/resume".Length..].Trim();
        if (raw.Length == 0)
        {
            var sessions = agents.List();
            if (sessions.Count == 0)
            {
                AppendRaw("  no live sessions\n");
                return;
            }

            AppendRaw(string.Join('\n', sessions.Select(agent =>
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
            try
            {
                handle = await agents.Resume(new ResumeAgentOptions(
                    sessionId,
                    new AgentOptions(_agent.Options.Provider, _agent.Options.Model, _agent.Options.ReasoningEffort, _agent.Options.MaxTokens)));
            }
            catch (Exception error)
            {
                AppendRaw($"  session cannot be loaded: {raw} — {error.Message}\n");
                return;
            }

            target = (AgentLoopAgent)handle.Agent;
            await target.WhenIdle();
        }

        QueueAction(() =>
        {
            SwitchAgent(target);
            AppendRaw($"  resumed: {target.Id}\n");
        });
        await Task.CompletedTask;
    }

    private async Task ListSessions(string text)
    {
        var raw = text["/session".Length..].Trim();
        if (raw.StartsWith("delete", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteSessionDelete(raw);
            return;
        }

        var sessions = CurrentSessions();
        if (raw.Length == 0)
        {
            if (sessions.Count == 0)
            {
                AppendRaw("  no sessions\n");
                return;
            }

            AppendRaw(string.Join('\n', sessions.Select(session =>
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
            AppendRaw($"  session not found: {raw}\n");
            return;
        }

        await ResumeSession($"/resume {match.Id}");
    }

    private async Task ExecuteSessionDelete(string raw)
    {
        var commands = _ctx.Get<CommandsService>(CommandsService.ServiceName);
        if (commands is null)
        {
            AppendRaw("  commands service unavailable\n");
            return;
        }

        try
        {
            var execution = await commands.Execute(_agent, $"/session {raw}");
            string? resultText = null;
            if (execution is null)
                resultText = $"  unknown command: /session {raw}\n";
            else if (execution.Result is CommandResult.Success { Text: { } successText } && successText.Length > 0)
                resultText = $"  {successText}\n";
            else if (execution.Result is CommandResult.Error error)
                resultText = $"  {error.Text}\n";
            if (resultText is not null)
                AppendRaw(resultText);
            if (execution?.Result is CommandResult.Success)
            {
                _commandMenu?.Close();
                _deleteConfirmSessionId = null;
                _sessionInfos = null;
            }
        }
        catch (Exception error)
        {
            AppendRaw($"  command failed: {error.Message}\n");
        }
    }

    /** /gpu [编号|auto]: 列出本机显卡并选择下次启动用的卡; 选择写设置文件(GUI 设置页共用同一键), 重启进程后生效。 */
    private void SelectGpu(string text)
    {
        var argument = text.Length > "/gpu".Length ? text["/gpu".Length..].Trim() : "";
        var adapters = GpuCatalog.ListAdapters();
        if (argument.Length == 0)
        {
            var current = GpuCatalog.LoadSelectedAdapter(_home);
            AppendRaw($"  gpu: current = {(current == GpuCatalog.AutoAdapter ? "auto (system default)" : current)}\n");
            AppendRaw("    0. auto (system default)\n");
            for (var index = 0; index < adapters.Count; index++)
            {
                var kind = GpuCatalog.IsDiscrete(adapters[index]) ? "discrete" : "integrated";
                AppendRaw($"    {index + 1}. {adapters[index].Name} ({kind}, {adapters[index].Detail})\n");
            }
            AppendRaw("  usage: /gpu <number> — takes effect after restart\n");
            return;
        }

        string selected;
        if (argument.Equals("auto", StringComparison.OrdinalIgnoreCase) || argument == "0")
            selected = GpuCatalog.AutoAdapter;
        else if (int.TryParse(argument, out var index) && index >= 1 && index <= adapters.Count)
            selected = adapters[index - 1].Name;
        else
        {
            AppendRaw($"  gpu: invalid selection '{argument}' (run /gpu to list)\n");
            return;
        }
        GpuCatalog.SaveSelectedAdapter(_home, selected);
        AppendRaw($"  gpu: selected {(selected == GpuCatalog.AutoAdapter ? "auto (system default)" : selected)} — takes effect after restart\n");
    }

    private async Task DetachSession(string text)
    {
        var commandLine = text["/detach".Length..].Trim();
        var parts = commandLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var fileName = parts.Length == 0
            ? Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh"
            : parts[0];
        var arguments = parts.Length > 1 ? new[] { parts[1] } : [];

        try
        {
            await PtyDaemonClient.EnsureRunningAsync();
            var session = await PtyDaemonClient.StartAsync(new PtyDaemonStartParams
            {
                FileName = fileName,
                Arguments = [.. arguments],
                WorkingDirectory = CurrentCwd(),
                Environment = new Dictionary<string, string?>
                {
                    ["DSH_DETACHED_SESSION_ID"] = _agent.Id.ToString(),
                },
            });
            AppendRaw($"  detached: {session.Id} — {session.Command} (daemon PTY)\n");
            RequestExit();
        }
        catch (Exception error)
        {
            AppendRaw($"  detach failed: {error.Message}\n");
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SetStatusReady();
    }

    private void DrawMain(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var transcriptVersion = _renderer.Version;
        if (_wrapCacheLines is null || _wrapCacheVersion != transcriptVersion || _wrapCacheWidth != rect.Width)
        {
            RebuildWrapCache(rect.Width);
            _wrapCacheVersion = transcriptVersion;
        }
        var wrapped = _wrapCacheLines!;
        if (wrapped.Count == 0)
            wrapped.Add("");

        var maxOffset = Math.Max(0, wrapped.Count - rect.Height);
        if (_stickToBottom)
            _scrollOffset = maxOffset;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxOffset);

        var start = _scrollOffset;
        for (var row = 0; row < rect.Height; row++)
        {
            var lineIndex = start + row;
            if (lineIndex >= wrapped.Count)
                break;
            DrawText(grid, rect.X, rect.Y + row, wrapped[lineIndex]);
        }

        if (_commandMenu?.IsActive == true || _mentionActive)
            DrawMenuOverlay(grid, rect);
    }

    private void DrawMenuOverlay(CellGrid grid, ConsoleRect rect)
    {
        if (_commandMenu?.IsActive == true)
        {
            PopupList.Draw(grid, rect, _commandMenu.Prompt, _commandMenu.Candidates, _commandMenu.SelectedIndex);
        }
        else if (_mentionActive)
        {
            PopupList.Draw(grid, rect, $"@ {_mentionCandidates.Count} candidates", _mentionCandidates, _mentionIndex);
        }
    }

    private void DrawRightPanel(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var row = rect.Y;
        var (provider, model) = CurrentModel(_agent);
        DrawPanelSection(grid, rect, ref row, "上下文",
        [
            $"session: {_agent.Id}",
            $"title: {_agent.Session.Header.Title ?? "-"}",
            $"cwd: {_agent.Session.Header.Cwd ?? "-"}",
            $"model: {provider}/{model}",
        ]);
        DrawPanelSection(grid, rect, ref row, "MCP", McpPanelLines());
        DrawPanelSection(grid, rect, ref row, "计划", ["使用 /plan 管理"]);
        DrawPanelSection(grid, rect, ref row, "输出", ["暂无"]);
        DrawPanelSection(grid, rect, ref row, "快捷键",
        [
            "Enter 发送 · / 命令 · @ 引用",
            "Tab 补全/折叠 · Esc 取消",
            "↑/↓ 历史 · PgUp/PgDn 滚动",
            "Ctrl+C×2 退出",
            "Ctrl+X N 新会话 · S 会话",
            "Ctrl+X D detach · K 删会话",
        ]);
    }

    private void DrawPanelSection(CellGrid grid, ConsoleRect rect, ref int row, string header, IReadOnlyList<string> lines)
    {
        if (row >= rect.Bottom)
            return;
        if (row > rect.Y && row + 1 < rect.Bottom)
        {
            for (var x = rect.X; x < rect.Right && x < grid.Width; x++)
                grid[x, row] = new Cell('─', AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
            row++;
        }
        DrawText(grid, rect.X, row, header, AnsiColor.BrightCyan, AnsiColor.Default, CellStyle.Bold);
        row++;
        foreach (var line in lines)
        {
            if (row >= rect.Bottom)
                return;
            DrawText(grid, rect.X, row, line, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
            row++;
        }
    }

    private IReadOnlyList<string> McpPanelLines()
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

    private void DrawDividers(CellGrid grid, UiLayout layout)
    {
        var verticalX = layout.RightPanel.Width > 0 ? layout.RightPanel.X - 1 : -1;
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

    private const string InputPrompt = "> ";

    private void DrawInput(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var prompt = _pendingApproval is not null ? "approval (y/n/c) " : InputPrompt;
        var caret = Math.Clamp(_cursor, 0, _input.Length);
        var availableWidth = Math.Max(0, rect.Width - prompt.Length);
        var textStart = caret;
        var visibleWidth = 0;
        while (textStart > 0)
        {
            var characterWidth = TerminalTextWidth.Of(_input[textStart - 1]);
            if (visibleWidth + characterWidth > availableWidth)
                break;
            visibleWidth += characterWidth;
            textStart--;
        }

        var visible = _input.AsSpan(textStart);

        DrawText(grid, rect.X, rect.Y, prompt, AnsiColor.Default, AnsiColor.Default, _pendingApproval is null ? CellStyle.None : CellStyle.Bold);
        DrawText(grid, rect.X + prompt.Length, rect.Y, visible);

        var caretColumn = TerminalTextWidth.Of(_input.AsSpan(textStart, caret - textStart));
        var cursorX = rect.X + Math.Min(prompt.Length + caretColumn, rect.Width - 1);
        var cursorY = rect.Y;
        cursorX = Math.Clamp(cursorX, 0, grid.Width - 1);
        cursorY = Math.Clamp(cursorY, 0, grid.Height - 1);
        var cursorCell = grid[cursorX, cursorY];
        grid[cursorX, cursorY] = cursorCell with { Style = cursorCell.Style | CellStyle.Reverse };
        CursorScreenX = cursorX;
        CursorScreenY = cursorY;

        if (rect.Height < 2)
            return;
        var infoY = rect.Y + 1;
        if (infoY >= grid.Height)
            return;
        var hint = "Enter 发送 · / 命令 · @ 引用 · Tab 补全 · Ctrl+X 会话";
        DrawText(grid, rect.X, infoY, hint, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
        var (currentProvider, currentModel) = CurrentModel(_agent);
        var profile = $"{currentProvider} · {currentModel}";
        var profileWidth = TerminalTextWidth.Of(profile);
        if (TerminalTextWidth.Of(hint) + profileWidth + 2 < rect.Width)
            DrawText(grid, rect.Right - profileWidth, infoY, profile, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
    }

    private int ColumnToCharIndex(int column)
    {
        var current = 0;
        for (var index = 0; index < _input.Length; index++)
        {
            var width = TerminalTextWidth.Of(_input[index]);
            if (current + width > column)
                return index;
            current += width;
        }

        return _input.Length;
    }

    private void DrawStatus(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        DrawText(grid, rect.X, rect.Y, _statusText, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
    }

    private static void DrawText(
        CellGrid grid,
        int x,
        int y,
        string text,
        AnsiColor foreground = AnsiColor.Default,
        AnsiColor background = AnsiColor.Default,
        CellStyle style = CellStyle.None)
        => DrawText(grid, x, y, text.AsSpan(), foreground, background, style);

    private static void DrawText(
        CellGrid grid,
        int x,
        int y,
        ReadOnlySpan<char> text,
        AnsiColor foreground = AnsiColor.Default,
        AnsiColor background = AnsiColor.Default,
        CellStyle style = CellStyle.None)
    {
        if (y < 0 || y >= grid.Height || x >= grid.Width)
            return;
        var column = Math.Max(0, x);
        foreach (var character in text)
        {
            if (column >= grid.Width)
                break;
            if (character == '\0')
                continue;
            grid[column, y] = new Cell(character, foreground, background, style);
            var width = TerminalTextWidth.Of(character);
            if (width == 2 && column + 1 < grid.Width)
                grid[column + 1, y] = new Cell('\0', foreground, background, style);
            column += width;
        }
    }

    private TranscriptFold? FindSelectedFold()
        => _selectedFoldKey is null ? null : _renderer.Folds.FirstOrDefault(fold => FoldKey(fold) == _selectedFoldKey);

    private void SelectNextFold(int direction)
    {
        var folds = _renderer.Folds;
        if (folds.Count == 0)
        {
            _selectedFoldKey = null;
            return;
        }

        var currentIndex = -1;
        for (var index = 0; index < folds.Count; index++)
        {
            if (FoldKey(folds[index]) == _selectedFoldKey)
            {
                currentIndex = index;
                break;
            }
        }

        var next = (currentIndex + direction + folds.Count) % folds.Count;
        _selectedFoldKey = FoldKey(folds[next]);
    }

    private static string FoldKey(TranscriptFold fold)
        => $"{fold.Start}:{fold.Label}";

    private sealed class VisualRow
    {
        public required int SourceStart { get; init; }
        public required int FlatStart { get; init; }
    }

    private readonly List<VisualRow> _visualRows = [];
    private readonly List<(int Start, int End, bool Collapsed)> _foldSnapshot = [];

    private void RebuildWrapCache(int width)
    {
        var lines = _renderer.CompletedLines;
        var starts = _renderer.LineStarts;
        var tail = _renderer.Tail;
        var tailStart = _renderer.TailStart;
        var folds = _renderer.Folds;
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

    /** offset → 行号: 行 i 覆盖 [starts[i], 下一行起点), tail 行覆盖 [tailStart, 末尾)。 */
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
        foreach (var fold in folds)
        {
            if (fold.Collapsed && fold.Start <= position && position < fold.End)
                return fold;
        }
        return null;
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

    private static void WrapSingleLine(string line, int width, List<string> output)
    {
        if (line.Length == 0)
        {
            output.Add("");
            return;
        }

        var start = 0;
        var column = 0;
        for (var index = 0; index < line.Length; index++)
        {
            var characterWidth = TerminalTextWidth.Of(line[index]);
            if (column + characterWidth > width)
            {
                output.Add(line[start..index]);
                start = index;
                column = 0;
            }

            column += characterWidth;
        }

        output.Add(line[start..]);
    }
}
