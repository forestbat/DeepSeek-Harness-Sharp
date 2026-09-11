using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Pty;
using Dsh.Skills;

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
    private TaskCompletionSource<ApprovalOutcome>? _pendingApproval;
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
    private int _hoveredTab = -1;
    private int _activeTab;
    private static readonly string[] PanelTabLabels = ["上下文", "MCP", "计划", "输出"];
    private string _statusText = "ready — Enter to send, ↑ history, Esc cancels a running turn, Ctrl+Q quits";
    private bool _exitRequested;
    private string? _deleteConfirmSessionId;
    private bool _sessionRenamedSubscribed;

    public ChatWindow(Context ctx, AgentLoopAgent agent, HarnessHome home, ISessionPersistence? persistence = null)
    {
        _ctx = ctx;
        _agent = agent;
        _home = home;
        _persistence = persistence;
        SubscribeSessionRenamed();
        LoadSkillCandidates();

        _unsubscribe = ctx.On(SessionStore.EventEvent, (_, args) =>
        {
            if (!ReferenceEquals(args[0], _agent.Session))
                return new ValueTask<object?>();
            var sessionEvent = (SessionEvent)args[1]!;
            QueueSessionEvent(sessionEvent);
            return new ValueTask<object?>();
        });

        _approvalSubscription = ctx.On(ApprovalEvents.Request, (_, args) =>
        {
            var request = (ApprovalRequest)args[0]!;
            var answer = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueAction(() => ShowApprovalPrompt(request, answer));
            return new ValueTask<object?>(answer.Task);
        }, new EventOptions { Global = true });
    }

    public bool ExitRequested => _exitRequested;

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

        var delta = _renderer.TakeDelta();
        if (delta.Length > 0)
            AppendText(delta);
    }

    public void HandleKey(ConsoleKeyInfo key)
    {
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

        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            switch (key.Key)
            {
                case ConsoleKey.Q:
                    RequestExit();
                    return;
                case ConsoleKey.C:
                    if (_busy)
                        _agent.Cancel(new AgentCancelCause.User());
                    else
                        RequestExit();
                    return;
            }
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
                    ConfirmCommandMenu();
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

    public void HandleMouseMove(int cellX, int cellY, UiLayout layout)
    {
        if (_pendingApproval is not null)
            return;
        _hoveredTab = HitPanelTab(cellX, cellY, layout.RightPanel);
    }

    public void HandleMouseClick(int cellX, int cellY, UiLayout layout)
    {
        if (_pendingApproval is not null)
            return;
        var tab = HitPanelTab(cellX, cellY, layout.RightPanel);
        if (tab >= 0)
        {
            _activeTab = tab;
            return;
        }

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

    public void Dispose()
    {
        UnsubscribeSessionRenamed();
        _unsubscribe.Invoke();
        _approvalSubscription.Invoke();
        _pendingApproval?.TrySetResult(ApprovalOutcome.Cancelled);
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

    private void ProcessSessionEvent(SessionEvent sessionEvent)
    {
        if (sessionEvent.Seq < _renderedSeq)
            return;
        _renderedSeq = sessionEvent.Seq;
        switch (sessionEvent.Data)
        {
            case TurnStartPayload:
                SetBusy(true);
                break;
            case TurnEndPayload:
                SetBusy(false);
                break;
        }

        _renderer.AppendSessionEvent(sessionEvent);
    }

    private void AppendRaw(string text)
    {
        AppendText(text);
    }

    private void AppendText(string text)
    {
        _renderer.AppendRaw(text);
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
        _unsubscribe = _ctx.On(SessionStore.EventEvent, (_, args) =>
        {
            if (!ReferenceEquals(args[0], _agent.Session))
                return new ValueTask<object?>();
            var sessionEvent = (SessionEvent)args[1]!;
            QueueSessionEvent(sessionEvent);
            return new ValueTask<object?>();
        });
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

    private void ShowApprovalPrompt(ApprovalRequest request, TaskCompletionSource<ApprovalOutcome> answer)
    {
        _pendingApproval = answer;
        AppendRaw($"  ⚠ approve tool \"{request.ToolName}\"?{(request.Reason is null ? "" : $" {request.Reason}")} [y]es/[n]o/[c]ancel turn\n");
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
                _ => [],
            };
        }
        catch (Exception)
        {
            return [];
        }
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
            _statusText = $"{_commandMenu.Prompt} — ↑/↓ Enter Esc";
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
            : "ready — Enter to send, ↑ history, Esc cancels a running turn, Ctrl+Q quits";

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
        var registry = _ctx.Get<SkillRegistry>(SkillRegistry.ServiceName);
        if (registry is null)
            return;
        _ = LoadSkillCandidatesAsync(registry);
    }

    private async Task LoadSkillCandidatesAsync(SkillRegistry registry)
    {
        try
        {
            var skills = await registry.List();
            _skillCandidates = skills.Select(skill => skill.Name).ToList();
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
        var delta = _renderer.TakeDelta();
        if (delta.Length > 0)
            AppendText(delta);

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

            AppendRaw(string.Join('\n', sessions.Select(agent => $"  {agent.Id}: {agent.Options.Provider}/{agent.Options.Model}")) + "\n");
            return;
        }

        var sessionId = SessionId.Create(raw);
        if (agents.Get(sessionId) is not AgentLoopAgent target)
        {
            AppendRaw($"  session not loaded: {raw}\n");
            return;
        }

        SwitchAgent(target);
        AppendRaw($"  resumed: {target.Id}\n");
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

        var visible = BuildVisibleLines(_renderer.FullText, _renderer.Folds);
        var wrapped = WrapLines(visible, rect.Width);
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

        DrawPanelTabs(grid, rect);
        if (rect.Height > 1)
        {
            for (var x = rect.X; x < rect.Right && x < grid.Width; x++)
                grid[x, rect.Y + 1] = new Cell('─', AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
        }

        DrawPanelContent(grid, rect);
    }

    private void DrawPanelTabs(CellGrid grid, ConsoleRect rect)
    {
        var x = rect.X;
        for (var index = 0; index < PanelTabLabels.Length; index++)
        {
            var label = $"[{PanelTabLabels[index]}]";
            var width = TerminalTextWidth.Of(label);
            if (x + width > rect.Right)
                break;
            var style = index == _activeTab ? CellStyle.Bold : index == _hoveredTab ? CellStyle.Reverse : CellStyle.None;
            var foreground = index == _activeTab ? AnsiColor.BrightCyan : AnsiColor.Default;
            DrawText(grid, x, rect.Y, label, foreground, AnsiColor.Default, style);
            x += width;
        }
    }

    private void DrawPanelContent(CellGrid grid, ConsoleRect rect)
    {
        var contentY = rect.Y + 2;
        if (contentY >= rect.Bottom)
            return;

        var lines = _activeTab switch
        {
            0 =>
                new[]
                {
                    $"session: {_agent.Id}",
                    $"title: {_agent.Session.Header.Title ?? "-"}",
                    $"cwd: {_agent.Session.Header.Cwd ?? "-"}",
                    $"model: {_agent.Options.Provider}/{_agent.Options.Model}",
                },
            1 => new[] { "MCP servers: 使用 /mcp 管理" },
            2 => new[] { "计划: 使用 /plan 管理" },
            _ => new[] { "输出: 暂无" },
        };

        var row = contentY;
        foreach (var line in lines)
        {
            if (row >= rect.Bottom)
                break;
            DrawText(grid, rect.X, row, line, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
            row++;
        }
    }

    private int HitPanelTab(int cellX, int cellY, ConsoleRect rect)
    {
        if (rect.Width <= 0 || cellY != rect.Y || cellX < rect.X || cellX >= rect.Right)
            return -1;
        var x = rect.X;
        for (var index = 0; index < PanelTabLabels.Length; index++)
        {
            var width = TerminalTextWidth.Of($"[{PanelTabLabels[index]}]");
            if (x + width > rect.Right)
                break;
            if (cellX >= x && cellX < x + width)
                return index;
            x += width;
        }

        return -1;
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

        var visible = _input.Length == 0 ? "" : _input[textStart..];

        DrawText(grid, rect.X, rect.Y, prompt, AnsiColor.Default, AnsiColor.Default, _pendingApproval is null ? CellStyle.None : CellStyle.Bold);
        DrawText(grid, rect.X + prompt.Length, rect.Y, visible);

        var caretColumn = TerminalTextWidth.Of(_input[textStart..caret]);
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
        var hint = "Enter 发送 · / 命令 · @ 引用 · Tab 折叠";
        DrawText(grid, rect.X, infoY, hint, AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
        var profile = $"{_agent.Options.Provider} · {_agent.Options.Model}";
        var profileWidth = TerminalTextWidth.Of(profile);
        if (profileWidth < rect.Width)
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

    private static List<string> BuildVisibleLines(string text, IReadOnlyList<TranscriptFold> folds)
    {
        var source = SplitLines(text);
        var collapsed = folds.Where(fold => fold.Collapsed).ToList();
        var result = new List<string>();
        var offset = 0;
        for (var index = 0; index < source.Count; index++)
        {
            var line = source[index];
            var lineStart = offset;
            var fold = collapsed.FirstOrDefault(candidate => lineStart >= candidate.Start && lineStart < candidate.End);
            if (fold is null)
            {
                result.Add(line);
                offset += line.Length + 1;
                continue;
            }

            if (lineStart == fold.Start)
                result.Add(fold.Preview);
            while (index < source.Count && offset < fold.End)
            {
                offset += source[index].Length + 1;
                index++;
            }

            index--;
        }

        return result;
    }

    private static List<string> SplitLines(string text)
        => string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Split('\n').ToList();

    private static List<string> WrapLines(IReadOnlyList<string> lines, int width)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                result.Add("");
                continue;
            }

            var start = 0;
            var column = 0;
            for (var index = 0; index < line.Length; index++)
            {
                var characterWidth = TerminalTextWidth.Of(line[index]);
                if (column + characterWidth > width)
                {
                    result.Add(line[start..index]);
                    start = index;
                    column = 0;
                }

                column += characterWidth;
            }

            result.Add(line[start..]);
        }

        return result;
    }
}