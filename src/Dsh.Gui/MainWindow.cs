using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using AvaloniaTextBlock = Avalonia.Controls.TextBlock;
using LlmTextBlock = Dsh.Llm.TextBlock;

namespace Dsh.Gui;

public sealed class MainWindow : Window
{
    private readonly Context _ctx;
    private readonly object _eventGate = new();
    private readonly Queue<SessionEvent> _pendingEvents = [];
    private readonly List<Func<bool>> _unsubscribers = [];
    private readonly List<MessageEntry> _messageEntries = [];

    private AgentLoopAgent _agent;
    private bool _eventScheduled;
    private bool _busy;
    private long _renderedSeq;
    private TaskCompletionSource<ApprovalOutcome>? _pendingApproval;

    private readonly ListBox _sessionList = new();
    private readonly ListBox _messagesList = new();
    private readonly TextBox _input = new() { Watermark = "输入消息，/ 开头为命令（Enter 发送）" };
    private readonly AvaloniaTextBlock _status = new() { Text = "准备就绪", Margin = new Thickness(4) };
    private readonly SettingsPanel _settingsPanel;
    private readonly Button _cancelButton = new() { Content = "取消", IsEnabled = false };

    public MainWindow(Context ctx, AgentLoopAgent agent, HarnessHome home)
    {
        _ctx = ctx;
        _agent = agent;
        Title = $"DeepSeek Harness - {agent.Options.Provider}/{agent.Options.Model}";
        Width = 1280;
        Height = 800;
        MinWidth = 960;
        MinHeight = 600;
        _settingsPanel = new SettingsPanel(ctx, agent, home);

        var root = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(260)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(360)),
            },
        };
        root.Children.Add(BuildSessionColumn());
        root.Children.Add(BuildChatColumn());
        root.Children.Add(_settingsPanel);
        Grid.SetColumn(root.Children[1], 1);
        Grid.SetColumn(root.Children[2], 2);
        Content = root;

        _settingsPanel.SettingsChanged += RefreshSessions;
        Subscribe();
        RefreshSessions();
        ShowAgent(_agent);
    }

    private Control BuildSessionColumn()
    {
        var dock = new DockPanel();
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4),
            Children =
            {
                new AvaloniaTextBlock { Text = "会话", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold },
                new Button { Content = "新建", Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right },
            },
        };
        ((Button)header.Children[1]).Click += async (_, _) => await NewSessionAsync();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        _sessionList.SelectionChanged += (_, _) =>
        {
            if (_sessionList.SelectedItem is ListBoxItem { Tag: AgentLoopAgent agent } && !ReferenceEquals(agent, _agent))
                ShowAgent(agent);
        };
        DockPanel.SetDock(_sessionList, Dock.Bottom);
        dock.Children.Add(_sessionList);
        return dock;
    }

    private Control BuildChatColumn()
    {
        var dock = new DockPanel();
        var bottom = new DockPanel { Margin = new Thickness(4) };
        var sendButton = new Button { Content = "发送", Width = 72, Margin = new Thickness(8, 0, 0, 0) };
        sendButton.Click += (_, _) => Submit();
        DockPanel.SetDock(sendButton, Dock.Right);
        _input.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter && key.KeyModifiers == KeyModifiers.None)
            {
                Submit();
                key.Handled = true;
            }
        };
        bottom.Children.Add(_input);
        bottom.Children.Add(sendButton);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);

        var statusDock = new DockPanel();
        DockPanel.SetDock(_status, Dock.Left);
        DockPanel.SetDock(_cancelButton, Dock.Right);
        statusDock.Children.Add(_status);
        statusDock.Children.Add(_cancelButton);
        DockPanel.SetDock(statusDock, Dock.Bottom);
        dock.Children.Add(statusDock);

        dock.Children.Add(_messagesList);
        _cancelButton.Click += (_, _) => _agent.Cancel(new AgentCancelCause.User());
        return dock;
    }

    private void Subscribe()
    {
        _unsubscribers.Add(_ctx.On(SessionStore.EventEvent, (_, args) =>
        {
            if (!ReferenceEquals(args[0], _agent.Session))
                return new ValueTask<object?>();
            var sessionEvent = (SessionEvent)args[1]!;
            QueueSessionEvent(sessionEvent);
            return new ValueTask<object?>();
        }));
        _unsubscribers.Add(_ctx.On(ApprovalEvents.Request, (_, args) =>
        {
            var request = (ApprovalRequest)args[0]!;
            var answer = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() => ShowApprovalPrompt(request, answer));
            return new ValueTask<object?>(answer.Task);
        }, new EventOptions { Global = true }));
    }

    private void QueueSessionEvent(SessionEvent sessionEvent)
    {
        lock (_eventGate)
        {
            _pendingEvents.Enqueue(sessionEvent);
            if (_eventScheduled)
                return;
            _eventScheduled = true;
            Dispatcher.UIThread.Post(DrainPendingEvents);
        }
    }

    private void DrainPendingEvents()
    {
        lock (_eventGate)
        {
            while (_pendingEvents.Count > 0)
                ProcessSessionEvent(_pendingEvents.Dequeue());
            _eventScheduled = false;
        }
    }

    private void ProcessSessionEvent(SessionEvent sessionEvent)
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
            case UserMessagePayload user:
                AddMessage("You", ContentText(user.Message.Content), "user");
                break;
            case AssistantChunkPayload chunk:
                AppendAssistantChunk(chunk.Chunk);
                break;
            case AssistantMessagePayload assistant:
                AddAssistantMessage(ContentText(assistant.Message.Content));
                break;
            case ToolCallPayload call:
                AddMessage("Tool", $"{call.Name} {call.Arguments}", "tool");
                break;
            case ToolResultPayload result:
                AddMessage("Result", result.Error is null
                    ? ContentText(result.Message.Content)
                    : $"[{result.Error.Name}: {result.Error.Code}] {ContentText(result.Message.Content)}", "result");
                break;
        }
    }

    private void AddMessage(string role, string text, string kind)
    {
        var entry = new MessageEntry(role, text, kind, false);
        _messageEntries.Add(entry);
        AddEntry(entry);
    }

    private void AddAssistantMessage(string text)
    {
        if (_messageEntries.Count > 0 && _messageEntries[^1].Kind == "assistant" && _messageEntries[^1].Streaming)
        {
            _messageEntries[^1].SetText(text);
            return;
        }
        var entry = new MessageEntry("Assistant", text, "assistant", false);
        _messageEntries.Add(entry);
        AddEntry(entry);
    }

    private void AppendAssistantChunk(StreamChunk chunk)
    {
        if (chunk is not StreamChunk.TextDelta { Text: { Length: > 0 } delta })
            return;
        if (_messageEntries.Count > 0 && _messageEntries[^1].Kind == "assistant" && _messageEntries[^1].Streaming)
        {
            _messageEntries[^1].Append(delta);
        }
        else
        {
            var entry = new MessageEntry("Assistant", delta, "assistant", true);
            _messageEntries.Add(entry);
            AddEntry(entry);
        }
        ScrollToEnd();
    }

    private void AddEntry(MessageEntry entry)
    {
        var item = new ListBoxItem { Content = entry.TextBlock, Tag = entry };
        _messagesList.Items.Add(item);
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (_messagesList.ItemCount == 0)
            return;
        if (_messagesList.Items[^1] is { } last)
            _messagesList.ScrollIntoView(last);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _cancelButton.IsEnabled = busy;
        _status.Text = busy ? "工作中…" : "准备就绪";
    }

    private void Submit()
    {
        var text = _input.Text?.Trim() ?? "";
        if (text.Length == 0)
            return;
        _input.Text = "";
        if (text.StartsWith('/'))
        {
            _ = RunCommandAsync(text);
            return;
        }

        var message = MessageFactory.CreateUserText(text);
        AddMessage("You", text, "user");
        SetBusy(true);
        _agent.Followup(message);
    }

    private async Task RunCommandAsync(string text)
    {
        var commands = _ctx.Get<CommandsService>(CommandsService.ServiceName);
        if (commands is null)
        {
            AddMessage("System", $"未知命令: {text}", "system");
            return;
        }

        try
        {
            var execution = await commands.Execute(_agent, text);
            if (execution is null)
                AddMessage("System", $"未知命令: {text}", "system");
            else if (execution.Result is CommandResult.Success { Text: { Length: > 0 } success })
                AddMessage("System", success, "system");
            else if (execution.Result is CommandResult.Error error)
                AddMessage("System", error.Text, "system");
        }
        catch (Exception error)
        {
            AddMessage("System", $"命令失败: {error.Message}", "system");
        }
    }

    private async Task NewSessionAsync()
    {
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            Environment.CurrentDirectory,
            new AgentOptions(_agent.Options.Provider, _agent.Options.Model, _agent.Options.ReasoningEffort, _agent.Options.MaxTokens)));
        var newAgent = (AgentLoopAgent)handle.Agent;
        await newAgent.WhenIdle();
        RefreshSessions();
        ShowAgent(newAgent);
    }

    private void ShowAgent(AgentLoopAgent agent)
    {
        _agent = agent;
        Title = $"DeepSeek Harness - {agent.Options.Provider}/{agent.Options.Model}";
        _messageEntries.Clear();
        _messagesList.Items.Clear();
        _renderedSeq = 0;
        _settingsPanel.Agent = agent;
        foreach (var sessionEvent in agent.Session.SnapshotEvents())
            ProcessSessionEvent(sessionEvent);
        SetBusy(agent.Status == AgentStatus.Running);
        SelectSession(agent);
        _input.Focus();
    }

    private void RefreshSessions()
    {
        var selected = _agents().ToList();
        _sessionList.Items.Clear();
        foreach (var agent in selected)
        {
            var item = new ListBoxItem
            {
                Tag = agent,
                Content = new AvaloniaTextBlock
                {
                    Text = $"{agent.Id}\n{agent.Options.Provider}/{agent.Options.Model}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4),
                },
            };
            _sessionList.Items.Add(item);
            if (ReferenceEquals(agent, _agent))
                _sessionList.SelectedItem = item;
        }
    }

    private IEnumerable<AgentLoopAgent> _agents()
        => _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)?.List().OfType<AgentLoopAgent>() ?? [];

    private void SelectSession(AgentLoopAgent agent)
    {
        foreach (var item in _sessionList.Items.OfType<ListBoxItem>())
        {
            if (ReferenceEquals(item.Tag, agent))
            {
                _sessionList.SelectedItem = item;
                break;
            }
        }
    }

    private void ShowApprovalPrompt(ApprovalRequest request, TaskCompletionSource<ApprovalOutcome> answer)
    {
        _pendingApproval = answer;
        AddMessage("Approval", $"批准工具 \"{request.ToolName}\"?{(request.Reason is null ? "" : $" {request.Reason}")}", "system");
        var dialog = new ApprovalDialog(request, _ => AnswerApproval(ApprovalOutcome.AllowedOnce));
        dialog.ShowDialog(this);
        _status.Text = $"等待批准 \"{request.ToolName}\"";
    }

    private void AnswerApproval(ApprovalOutcome outcome)
    {
        var pending = _pendingApproval;
        if (pending is null)
            return;
        _pendingApproval = null;
        pending.TrySetResult(outcome);
        _status.Text = _busy ? "工作中…" : "准备就绪";
    }

    private static string ContentText(IReadOnlyList<ContentBlock> blocks)
        => string.Join('\n', blocks.Select(block => block switch
        {
            LlmTextBlock text => text.Text,
            ReasoningBlock reasoning => $"[reasoning] {reasoning.Text}",
            ToolCallBlock call => $"[tool: {call.Name}] {call.Arguments}",
            ToolResultBlock result => ContentText(result.Content),
            ImageBlock => "[image]",
            _ => $"[{block.Type}]",
        }));

    protected override void OnClosed(EventArgs e)
    {
        foreach (var unsubscribe in _unsubscribers)
            unsubscribe();
        _pendingApproval?.TrySetResult(ApprovalOutcome.Cancelled);
        base.OnClosed(e);
    }

    private sealed class MessageEntry
    {
        public string Kind { get; }
        public bool Streaming { get; }
        public AvaloniaTextBlock TextBlock { get; }

        private readonly string _prefix;

        public MessageEntry(string role, string text, string kind, bool streaming)
        {
            _prefix = $"{role}: ";
            Kind = kind;
            Streaming = streaming;
            TextBlock = new AvaloniaTextBlock
            {
                Text = _prefix + text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4),
            };
        }

        public void Append(string text) => TextBlock.Text += text;

        public void SetText(string text) => TextBlock.Text = _prefix + text;
    }
}