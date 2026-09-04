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
    private readonly AgentLoopAgent _agent;
    private readonly TranscriptRenderer _renderer = new();
    private readonly TextView _transcript;
    private readonly TextField _input;
    private readonly Label _status;
    private readonly List<string> _history = [];
    private readonly Func<bool> _unsubscribe;
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
            if (!ReferenceEquals(args[0], agent.Session))
                return new ValueTask<object?>();
            var sessionEvent = (SessionEvent)args[1]!;
            Application.Invoke(() => OnSessionEvent(sessionEvent));
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
            ? (direction < 0 ? _history.Count - 1 : -1)
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
        RefreshTranscript();
        if (text.StartsWith('/'))
        {
            RunSlashCommand(text);
            return;
        }
        SetBusy(true);
        _agent.Followup(message);
    }

    private void RunSlashCommand(string text)
    {
        switch (text.Split(' ', 2)[0])
        {
            case "/quit" or "/exit":
                Application.RequestStop(this);
                break;
            default:
                AppendRaw($"  unknown command: {text} (available: /quit, /exit)\n");
                break;
        }
    }

    private void OnSessionEvent(SessionEvent sessionEvent)
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
        RefreshTranscript();
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
        _transcript.Text += text;
        ScrollToEnd();
    }

    private void RefreshTranscript()
    {
        _transcript.Text = _renderer.Text;
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        _transcript.MoveEnd();
        _transcript.SetNeedsDraw();
    }
}
