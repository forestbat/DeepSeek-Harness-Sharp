using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Dsh.Tui;

public sealed class ChatWindow : Window
{
    private readonly Context _ctx;
    private AgentLoopAgent _agent;
    private TranscriptRenderer _renderer = new();
    private readonly IRenderOptimizer _renderOptimizer = RenderOptimizerFactory.Create();
    private readonly object _eventGate = new();
    private readonly Queue<SessionEvent> _pendingEvents = [];
    private bool _eventScheduled;
    private readonly TextView _transcript;
    private readonly TextField _input;
    private readonly Label _status;
    private readonly List<string> _history = [];
    private Func<bool> _unsubscribe;
    private readonly Func<bool> _approvalSubscription;
    private TaskCompletionSource<ApprovalOutcome>? _pendingApproval;
    private int _historyIndex = -1;
    private bool _busy;
    private long _renderedSeq;

    public ChatWindow(Context ctx, AgentLoopAgent agent, string model)
    {
        _ctx = ctx;
        _agent = agent;
        Title = $"dsh — {model}";

        _transcript = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            Multiline = true,
            CanFocus = false,
        };
        _input = new TextField
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
        };
        _status = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = "ready — Enter to send, ↑ history, Esc cancels a running turn, Ctrl+Q quits",
        };
        Add(_transcript, _input, _status);

        Initialized += (_, _) => _input.SetFocus();
        _input.KeyDown += (_, key) => OnInputKey(key);

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
            Application.Invoke(() => ShowApprovalPrompt(request, answer));
            return new ValueTask<object?>(answer.Task);
        }, new EventOptions { Global = true });
    }

    private void QueueSessionEvent(SessionEvent sessionEvent)
    {
        lock (_eventGate)
        {
            _pendingEvents.Enqueue(sessionEvent);
            if (_eventScheduled)
                return;
            _eventScheduled = true;
            Application.Invoke(DrainPendingEvents);
        }
    }

    private void DrainPendingEvents()
    {
        lock (_eventGate)
        {
            while (true)
            {
                while (_pendingEvents.Count > 0)
                    ProcessSessionEvent(_pendingEvents.Dequeue());
                AppendRendererDelta();
                if (_pendingEvents.Count == 0)
                    break;
            }
            _eventScheduled = false;
        }
    }

    private void ClearPendingEvents()
    {
        lock (_eventGate)
        {
            _pendingEvents.Clear();
            _eventScheduled = false;
        }
    }

    private void SwitchAgent(AgentLoopAgent agent, string model)
    {
        _unsubscribe();
        ClearPendingEvents();
        _agent = agent;
        Title = $"dsh — {model}";
        _renderer = new TranscriptRenderer();
        _transcript.Text = "";
        _renderedSeq = 0;
        _unsubscribe = _ctx.On(SessionStore.EventEvent, (_, args) =>
        {
            if (!ReferenceEquals(args[0], _agent.Session))
                return new ValueTask<object?>();
            var sessionEvent = (SessionEvent)args[1]!;
            QueueSessionEvent(sessionEvent);
            return new ValueTask<object?>();
        });
    }

    private void ShowApprovalPrompt(ApprovalRequest request, TaskCompletionSource<ApprovalOutcome> answer)
    {
        _pendingApproval = answer;
        AppendRaw($"  ⚠ approve tool \"{request.ToolName}\"?{(request.Reason is null ? "" : $" {request.Reason}")} [y]es/[n]o/[c]ancel turn\n");
        _status.Text = $"approval pending for \"{request.ToolName}\" — y/n/c";
        _input.SetFocus();
    }

    private void AnswerApproval(ApprovalOutcome outcome)
    {
        var pending = _pendingApproval;
        if (pending is null)
            return;
        _pendingApproval = null;
        AppendRaw($"  approval: {outcome}\n");
        _status.Text = _busy
            ? "working… (Esc to cancel)"
            : "ready — Enter to send, ↑ history, Esc cancels a running turn, Ctrl+Q quits";
        pending.TrySetResult(outcome);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _unsubscribe();
            _approvalSubscription();
            _pendingApproval?.TrySetResult(ApprovalOutcome.Cancelled);
        }
        base.Dispose(disposing);
    }

    private void OnInputKey(Key key)
    {
        if (_pendingApproval is not null)
        {
            if (key == Key.Y) AnswerApproval(ApprovalOutcome.AllowedOnce);
            else if (key == Key.N) AnswerApproval(ApprovalOutcome.Rejected);
            else if (key == Key.C || key == Key.Esc) AnswerApproval(ApprovalOutcome.Cancelled);
            else return;
            key.Handled = true;
            return;
        }
        if (key == Key.Enter)
        {
            Submit();
            key.Handled = true;
        }
        else if (key == Key.Esc && _busy)
        {
            _agent.Cancel(new AgentCancelCause.User());
            key.Handled = true;
        }
        else if (key == Key.CursorUp)
        {
            RecallHistory(-1);
            key.Handled = true;
        }
        else if (key == Key.CursorDown)
        {
            RecallHistory(1);
            key.Handled = true;
        }
    }

    private void RecallHistory(int direction)
    {
        if (_history.Count == 0)
            return;
        _historyIndex = _historyIndex < 0
            ? direction < 0 ? _history.Count - 1 : -1
            : Math.Clamp(_historyIndex + direction, -1, _history.Count - 1);
        _input.Value = _historyIndex < 0 ? "" : _history[_historyIndex];
        _input.MoveEnd();
    }

    private void Submit()
    {
        var text = _input.Value?.Trim() ?? "";
        if (text.Length == 0)
            return;
        _input.Value = "";
        _history.Add(text);
        _historyIndex = -1;
        var message = MessageFactory.CreateUserText(text);
        _renderer.AppendUserMessage(message);
        AppendRendererDelta();
        if (text.StartsWith('/'))
        {
            RunSlashCommand(text);
            return;
        }
        SetBusy(true);
        _agent.Followup(message);
    }

    private async void RunSlashCommand(string text)
    {
        switch (text.Split(' ', 2)[0])
        {
            case "/quit" or "/exit":
                Application.RequestStop(this);
                break;
            case "/new":
                await NewSession(text);
                break;
            case "/resume":
                await ResumeSession(text);
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
                    if (execution is null)
                        AppendRaw($"  unknown command: {text}\n");
                    else if (execution.Result is CommandResult.Success { Text: { } successText } && successText.Length > 0)
                        AppendRaw($"  {successText}\n");
                    else if (execution.Result is CommandResult.Error error)
                        AppendRaw($"  {error.Text}\n");
                }
                catch (Exception error)
                {
                    AppendRaw($"  command failed: {error.Message}\n");
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
        SwitchAgent(newAgent, $"{newAgent.Options.Provider}/{newAgent.Options.Model}");
        AppendRaw($"  new session: {newAgent.Id}\n");
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
        SwitchAgent(target, $"{target.Options.Provider}/{target.Options.Model}");
        AppendRaw($"  resumed: {target.Id}\n");
        await Task.CompletedTask;
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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _status.Text = busy
            ? "working… (Esc to cancel)"
            : "ready — Enter to send, ↑ history, Esc cancels a running turn, Ctrl+Q quits";
    }

    private void AppendRaw(string text)
    {
        _renderOptimizer.Append(_transcript, text);
        ScrollToEnd();
    }

    private void AppendRendererDelta()
    {
        var delta = _renderer.TakeDelta();
        if (delta.Length == 0)
            return;
        _renderOptimizer.Append(_transcript, delta);
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        _transcript.MoveEnd();
        _transcript.SetNeedsDraw();
    }
}
