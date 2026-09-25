using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Pty;
using Dsh.Subagent;

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
    private readonly MentionResolver _mentionResolver = new();
    private readonly ISessionPersistence? _persistence;
    private readonly SubagentDirectory _subagents;
    private readonly HashSet<SessionId> _liveSubagents = [];
    private readonly Dictionary<int, ITuiPane> _panes = [];
    private readonly List<OverviewItem> _overviewItems = [];
    private readonly List<SubagentNode> _agentItems = [];
    private IReadOnlyList<SessionInfo>? _sessionInfos;
    private IReadOnlyList<string> _skillCandidates = [];
    private IReadOnlyList<string>? _mcpPanelLines;
    private IReadOnlyList<PtyDaemonSessionDto> _daemonPtys = [];
    private PaneNode _paneTree = new PaneLeaf(0);
    private PaneLayout _paneLayout;
    private ConsoleRect _paneArea;
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

    public ChatWindow(Context ctx, AgentLoopAgent agent, HarnessHome home, ISessionPersistence? persistence = null)
    {
        _ctx = ctx;
        _home = home;
        _persistence = persistence;
        _subagents = new SubagentDirectory(ctx);
        LoadSkillCandidates();

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

    public int RenderVersion { get; private set; }

    public int CursorScreenX => InputPane.CursorScreenX;

    public int CursorScreenY => InputPane.CursorScreenY;

    internal Context Ctx => _ctx;

    internal HarnessHome Home => _home;

    internal MentionResolver MentionResolver => _mentionResolver;

    internal IReadOnlyList<string> SkillCandidates => _skillCandidates;

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

    public void HandleKey(ConsoleKeyInfo key)
    {
        RenderVersion++;
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
                    input.StatusText = "Ctrl+X: N 新会话 · S 会话 · D detach · K 删会话 · A 子代理 · W 总览 · + 分屏 · - 关窗格 · 方向键/O 切窗格";
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
                RunSlashCommand(input, "/detach");
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

    public void InsertText(string text)
    {
        RenderVersion++;
        InputPane.InsertText(text);
    }

    public void HandleMouseWheel(int delta)
    {
        RenderVersion++;
        FocusedPane.HandleMouseWheel(delta);
    }

    /** 鼠标滚轮按指针所在 pane 路由; 无命中时回退焦点 pane。 */
    public void HandleMouseWheel(int delta, int cellX, int cellY, UiLayout layout)
    {
        RenderVersion++;
        var target = _panes.Count > 1 ? _paneLayout.HitTest(cellX, cellY) : null;
        if (target is { } id && _panes.TryGetValue(id, out var pane))
            pane.HandleMouseWheel(delta);
        else
            FocusedPane.HandleMouseWheel(delta);
    }

    public void HandleMouseClick(int cellX, int cellY, UiLayout layout)
    {
        RenderVersion++;
        var input = InputPane;
        if (input.PendingApproval is not null)
            return;
        if (layout.Input.Contains(cellX, cellY))
        {
            input.HandleInputClick(cellX, layout.Input);
            return;
        }

        if (_panes.Count == 1)
        {
            if (layout.Main.Contains(cellX, cellY))
                input.StickToBottom = false;
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

        FocusedPane.StickToBottom = false;
    }

    public void Draw(CellGrid grid, UiLayout layout)
    {
        grid.Clear();
        _paneArea = new ConsoleRect(0, 0, grid.Width, Math.Max(0, layout.Main.Height));
        _paneLayout = LayoutEngine.EvaluatePanes(_paneTree, _paneArea);

        if (_panes.Count == 1)
        {
            var only = _panes.Values.First();
            only.DrawTranscript(grid, layout.Main);
            if (only is ChatPane chatPane)
                chatPane.DrawRightPanel(grid, layout.RightPanel);
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
    }

    public void RequestExit()
        => _exitRequested = true;

    public void Dispose()
    {
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
    }

    internal IReadOnlyList<SessionInfo> CurrentSessions()
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

    internal void InvalidateSessionInfos()
        => _sessionInfos = null;

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
        switch (text.Split(' ', 2)[0])
        {
            case "/quit" or "/exit":
                RequestExit();
                break;
            case "/new":
                await NewSession(pane, text);
                break;
            case "/resume":
                await ResumeSession(pane, text);
                break;
            case "/session":
                await ListSessions(pane, text);
                break;
            case "/detach":
                await DetachSession(pane, text);
                break;
            case "/gpu":
                SelectGpu(pane, text);
                break;
            default:
                var commands = _ctx.Get<CommandsService>(CommandsService.ServiceName);
                if (commands is null)
                {
                    pane.AppendRaw($"  unknown command: {text} (available: /quit, /exit)\n");
                    break;
                }

                try
                {
                    var execution = await commands.Execute(pane.Agent, text);
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
                        QueueAction(() => pane.AppendRaw(captured));
                    }
                }
                catch (Exception error)
                {
                    var message = error.Message;
                    QueueAction(() => pane.AppendRaw($"  command failed: {message}\n"));
                }

                break;
        }
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
        if (argument.Length == 0)
        {
            var current = GpuCatalog.LoadSelectedAdapter(_home);
            pane.AppendRaw($"  gpu: current = {(current == GpuCatalog.AutoAdapter ? "auto (system default)" : current)}\n");
            pane.AppendRaw("    0. auto (system default)\n");
            for (var index = 0; index < adapters.Count; index++)
            {
                var kind = GpuCatalog.IsDiscrete(adapters[index]) ? "discrete" : "integrated";
                pane.AppendRaw($"    {index + 1}. {adapters[index].Name} ({kind}, {adapters[index].Detail})\n");
            }
            pane.AppendRaw("  usage: /gpu <number> — takes effect after restart\n");
            return;
        }

        string selected;
        if (argument.Equals("auto", StringComparison.OrdinalIgnoreCase) || argument == "0")
            selected = GpuCatalog.AutoAdapter;
        else if (int.TryParse(argument, out var index) && index >= 1 && index <= adapters.Count)
            selected = adapters[index - 1].Name;
        else
        {
            pane.AppendRaw($"  gpu: invalid selection '{argument}' (run /gpu to list)\n");
            return;
        }
        GpuCatalog.SaveSelectedAdapter(_home, selected);
        pane.AppendRaw($"  gpu: selected {(selected == GpuCatalog.AutoAdapter ? "auto (system default)" : selected)} — takes effect after restart\n");
    }

    private async Task DetachSession(ChatPane pane, string text)
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
                WorkingDirectory = pane.CurrentCwd(),
                Environment = new Dictionary<string, string?>
                {
                    ["DSH_DETACHED_SESSION_ID"] = pane.Agent.Id.ToString(),
                },
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
        SplitPaneAt(orientation, id);
        _panes[id] = new ChatPane(this, id, agent);
        _focusedPaneId = id;
        _inputPaneId = id;
        _paneLayout = LayoutEngine.EvaluatePanes(_paneTree, _paneArea);
        RenderVersion++;
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
        RenderVersion++;
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
            if (string.Equals(pane.Session.Id.Value, sessionId, StringComparison.OrdinalIgnoreCase))
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
        _overviewIndex = Math.Clamp(focusedIndex < 0 ? 0 : focusedIndex, 0, Math.Max(0, _overviewItems.Count - 1));
        _overviewActive = true;
        _ = RefreshDaemonPtysAsync();
    }

    private void HandleOverviewKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _overviewActive = false;
                return;
            case ConsoleKey.UpArrow:
                _overviewIndex = Math.Clamp(_overviewIndex - 1, 0, Math.Max(0, _overviewItems.Count - 1));
                return;
            case ConsoleKey.DownArrow:
                _overviewIndex = Math.Clamp(_overviewIndex + 1, 0, Math.Max(0, _overviewItems.Count - 1));
                return;
            case ConsoleKey.Enter:
                ActivateOverview();
                return;
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
        }
    }

    private void RefreshOverviewItems()
    {
        _overviewItems.Clear();
        _overviewItems.Add(new OverviewItem("PTY", OverviewTargetKind.None, null, null));
        var hostPtys = PtyHost.Default.List();
        if (hostPtys.Count == 0 && _daemonPtys.Count == 0)
        {
            _overviewItems.Add(new OverviewItem("  (无)", OverviewTargetKind.None, null, null));
        }
        foreach (var pty in hostPtys)
            _overviewItems.Add(new OverviewItem($"  {pty.Id.Value} · {pty.Command} · {pty.Status.ToString().ToLowerInvariant()}", OverviewTargetKind.None, null, null));
        foreach (var pty in _daemonPtys)
            _overviewItems.Add(new OverviewItem($"  {pty.Id} (daemon) · {pty.Command} · {pty.Status}", OverviewTargetKind.None, null, null));

        _overviewItems.Add(new OverviewItem("会话", OverviewTargetKind.None, null, null));
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)?.List() ?? [];
        if (agents.Count == 0)
            _overviewItems.Add(new OverviewItem("  (无)", OverviewTargetKind.None, null, null));
        foreach (var agent in agents)
        {
            var (provider, model) = CurrentModel(agent);
            _overviewItems.Add(new OverviewItem(
                $"  {agent.Id} · {provider}/{model}",
                OverviewTargetKind.Session,
                null,
                agent.Id.ToString()));
        }

        _overviewItems.Add(new OverviewItem("窗格", OverviewTargetKind.None, null, null));
        foreach (var id in PaneTree.PaneIds(_paneTree))
        {
            if (!_panes.TryGetValue(id, out var pane))
                continue;
            var marker = id == _focusedPaneId ? "* " : "  ";
            _overviewItems.Add(new OverviewItem(
                $"  {marker}pane {id} · {pane.Session.Id}",
                OverviewTargetKind.Pane,
                id,
                null));
        }
    }

    private async Task RefreshDaemonPtysAsync()
    {
        try
        {
            var sessions = await PtyDaemonClient.ListAsync();
            QueueAction(() =>
            {
                _daemonPtys = sessions;
                if (_overviewActive)
                    RefreshOverviewItems();
            });
        }
        catch (Exception error)
        {
            _ctx.LoggerFor("tui").Debug($"pty daemon list failed: {error.Message}");
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
        => _paneArea.Height > 0
            ? _paneArea
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
        var verticalX = _panes.Count == 1 && layout.RightPanel.Width > 0 ? layout.RightPanel.X - 1 : -1;
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
        Session,
    }

    private sealed record OverviewItem(string Line, OverviewTargetKind Kind, int? PaneId, string? SessionId);
}
