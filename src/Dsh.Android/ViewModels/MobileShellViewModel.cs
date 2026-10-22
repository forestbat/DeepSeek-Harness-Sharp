using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Android.Services;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;

namespace Dsh.Android.ViewModels;

public enum MobilePage
{
    Connect,
    Sessions,
    Chat,
}

/** 会话列表的一行: 登记表推送只改这一行, 不重建整个列表。 */
public sealed partial class SessionRowViewModel : ObservableObject
{
    public SessionRowViewModel(RemoteHostSession session)
    {
        Id = session.SessionId;
        Update(session);
    }

    public string Id { get; }

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _hasPending;

    [ObservableProperty]
    private string _activity = "";

    public string Kind { get; private set; } = "";

    public void Update(RemoteHostSession session)
    {
        Kind = session.Kind;
        Title = session.Title is { Length: > 0 } title ? title : session.ProjectPath ?? session.SessionId;
        Subtitle = session.ProjectPath ?? session.SessionId;
        Status = session.Status switch
        {
            HostSessionStatuses.Running => "运行中",
            HostSessionStatuses.WaitingForUser => "等待你",
            HostSessionStatuses.Idle => "空闲",
            HostSessionStatuses.Exited => "已退出",
            _ => session.Status,
        };
        HasPending = session.PendingCount > 0;
        Activity = FormatActivity(session.LastActivityAt);
    }

    private static string FormatActivity(long unixMilliseconds)
    {
        if (unixMilliseconds <= 0)
            return "";
        var time = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToLocalTime();
        return time.Date == DateTimeOffset.Now.Date ? time.ToString("HH:mm") : time.ToString("MM-dd HH:mm");
    }
}

/**
 * 手机端外壳: 连接桌面 host, 看会话列表, 读事件流, 发消息, 处理审批。
 * 网络事件先在后台线程入队, 由 DispatcherBridge 合并后一次投递到 UI 线程, 避免每个 token 一次 JNI/调度。
 */
public sealed partial class MobileShellViewModel : ObservableObject, IAsyncDisposable
{
    private const int MessageScrollThrottleMilliseconds = 80;

    private readonly EndpointStore _store;
    private readonly DispatcherBridge<RemoteEventInfo> _events;
    private readonly CancellationTokenSource _lifetime = new();
    private MobileHostConnection? _connection;
    private CancellationTokenSource? _sessionLifetime;
    private SubagentTranscript _transcript = new();
    private long _renderedSeq;
    private string _openSessionId = "";
    private string _approvalRequestId = "";
    private DateTime _lastScrollRequest;

    public MobileShellViewModel(EndpointStore store)
    {
        _store = store;
        var endpoint = store.Load();
        _host = endpoint.Host;
        _portText = endpoint.Port.ToString();
        _token = endpoint.Token;
        _events = new DispatcherBridge<RemoteEventInfo>(ApplyEvents);
        ShareInbox.Changed += DrainShares;
        DrainShares();
        // 保存过完整地址就自动连: 手机端没有稳定的桌面入口, 每次启动都要手点一次连接只会拖慢回到会话的速度。
        if (endpoint.IsComplete)
            _ = ConnectAsync();
    }

    /** 分享进来的文字/图片先在 UI 线程落到输入框或附件槽, 发送仍由用户确认。 */
    private void DrainShares()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(DrainShares);
            return;
        }
        while (ShareInbox.Take() is { } item)
        {
            if (item.Error.Length > 0)
            {
                Error = item.Error;
                continue;
            }
            if (item.Text.Length > 0)
            {
                Input = Input.Length == 0 ? item.Text : $"{Input}\n{item.Text}";
                continue;
            }
            if (item.Image.Length > 0 && item.MediaType.Length > 0)
                SetAttachment(item.Name, item.MediaType, item.Image);
        }
    }

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    /** 事件批次落到 UI 线程后触发, 视图据此把列表滚到底部。 */
    public event Action? TranscriptChanged;

    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    private string _portText = "";

    [ObservableProperty]
    private string _token = "";

    [ObservableProperty]
    private string _status = "未连接";

    [ObservableProperty]
    private IBrush _statusBrush = Brushes.Gray;

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    [ObservableProperty]
    private SessionRowViewModel? _selectedSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnect))]
    [NotifyPropertyChangedFor(nameof(ShowSessions))]
    [NotifyPropertyChangedFor(nameof(ShowChat))]
    private MobilePage _page = MobilePage.Connect;

    [ObservableProperty]
    private bool _hasApproval;

    [ObservableProperty]
    private string _approvalTitle = "";

    [ObservableProperty]
    private string _approvalDetail = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _hasAttachment;

    [ObservableProperty]
    private string _attachmentName = "";

    [ObservableProperty]
    private string? _attachmentMediaType;

    [ObservableProperty]
    private string _attachmentBase64 = "";

    public string AttachmentSummary => $"{AttachmentName} · {AttachmentBytes / 1024} KB";

    private int AttachmentBytes => AttachmentBase64.Length * 3 / 4;

    public bool ShowConnect => Page == MobilePage.Connect;

    public bool ShowSessions => Page == MobilePage.Sessions;

    public bool ShowChat => Page == MobilePage.Chat;

    public bool HasError => Error.Length > 0;

    public ObservableCollection<QuestionItemViewModel> Questions { get; } = [];

    [ObservableProperty]
    private string _questionTitle = "";

    [ObservableProperty]
    private string _questionRequestId = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAnswersCommand))]
    private bool _hasQuestions;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAnswersCommand))]
    private bool _isSendingAnswers;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    public bool IsConnected => _connection?.State == MobileConnectionState.Connected;

    public string ChatTitle => SelectedSession?.Title ?? "会话";

    public string ChatSubtitle => SelectedSession?.Status ?? "";

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await DisconnectAsync();
        _lifetime.Dispose();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!int.TryParse(PortText.Trim(), out var port) || port is <= 0 or > 65535)
        {
            Error = "端口无效";
            return;
        }
        var endpoint = new HostEndpoint(Host.Trim(), port, Token.Trim());
        if (!endpoint.IsComplete)
        {
            Error = "请填写主机与端口";
            return;
        }

        _store.Save(endpoint);
        await DisconnectAsync();
        var connection = new MobileHostConnection(endpoint);
        connection.Changed += OnConnectionChanged;
        _connection = connection;
        Error = "";
        Page = MobilePage.Sessions;
        Status = "连接中…";
        StatusBrush = Brushes.Orange;
        ConnectionServiceBridge.Publish(Status);
        if (MainActivity.IsForeground)
            ConnectionServiceBridge.Start();
        connection.Start();
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            connection.Changed -= OnConnectionChanged;
            await connection.DisposeAsync();
        }
        await CloseSessionAsync();
        ConnectionServiceBridge.Stop();
        Page = MobilePage.Connect;
        Status = "未连接";
        StatusBrush = Brushes.Gray;
        OnPropertyChanged(nameof(IsConnected));
        Sessions.Clear();
        HasApproval = false;
        foreach (var question in Questions)
            question.Changed -= OnAnswerChanged;
        Questions.Clear();
        QuestionRequestId = "";
        QuestionTitle = "";
        HasQuestions = false;
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        await CloseSessionAsync();
        Page = _connection is null ? MobilePage.Connect : MobilePage.Sessions;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if ((text.Length == 0 && !HasAttachment) || _connection is null || _openSessionId.Length == 0)
            return;
        IReadOnlyList<RemoteImageBlock> images = HasAttachment && AttachmentMediaType is { Length: > 0 } mediaType
            ? [new RemoteImageBlock(mediaType, 0, 0, AttachmentName, AttachmentBase64)]
            : [];
        Input = "";
        IsSending = true;
        try
        {
            var lease = await _connection.WaitConnectedAsync(_lifetime.Token);
            // 与桌面端一致: 先恢复 harness 会话(agent 还活着时是空操作), 否则发给已退出会话的消息会丢。
            if (SelectedSession?.Kind == HostSessionKinds.Harness)
                await lease.Client.ResumeSessionAsync(_openSessionId, null, null, _lifetime.Token);
            await lease.Client.SendMessageAsync(_openSessionId, text, images, _lifetime.Token);
            ClearAttachment();
            Error = "";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"发送失败: {error.Message}";
            Input = text;
        }
        finally
        {
            IsSending = false;
        }
    }

    private bool CanSend() => !IsSending && (Input.Trim().Length > 0 || HasAttachment) && _openSessionId.Length > 0;

    /** 由视图的文件选择器写入; 只保留图片本身, 不持有 Android 的 content URI 或流。 */
    public void SetAttachment(string name, string mediaType, byte[] bytes)
    {
        AttachmentName = name;
        AttachmentMediaType = mediaType;
        AttachmentBase64 = Convert.ToBase64String(bytes);
        HasAttachment = true;
        OnPropertyChanged(nameof(AttachmentSummary));
        Error = "";
    }

    public void ReportError(string message) => Error = message;

    public void ClearAttachment()
    {
        HasAttachment = false;
        AttachmentName = "";
        AttachmentMediaType = null;
        AttachmentBase64 = "";
        OnPropertyChanged(nameof(AttachmentSummary));
    }

    [RelayCommand]
    private Task AllowAsync() => RespondApprovalAsync(true);

    [RelayCommand]
    private Task DenyAsync() => RespondApprovalAsync(false);

    private async Task RespondApprovalAsync(bool allow)
    {
        if (_connection is null || _approvalRequestId.Length == 0)
            return;
        try
        {
            var lease = await _connection.WaitConnectedAsync(_lifetime.Token);
            await lease.Client.RespondApprovalAsync(_approvalRequestId, allow, reason: null, _lifetime.Token);
            Error = "";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"审批失败: {error.Message}";
        }
    }

    private void OnConnectionChanged() => Dispatcher.UIThread.Post(RefreshFromConnection);

    private void RefreshFromConnection()
    {
        var connection = _connection;
        if (connection is null)
            return;
        ApplyState(connection);
        ApplySessions(connection);
        ApplyPending(connection);
    }

    private void ApplyState(MobileHostConnection connection)
    {
        (Status, StatusBrush) = connection.State switch
        {
            MobileConnectionState.Connected => ("已连接", Brushes.MediumSeaGreen),
            MobileConnectionState.Connecting => ("连接中…", Brushes.Orange),
            MobileConnectionState.Reconnecting => ("重连中…", Brushes.Orange),
            _ => ("未连接", Brushes.Gray),
        };
        ConnectionServiceBridge.Publish(Status);
        if (connection.State == MobileConnectionState.Connected)
        {
            OnPropertyChanged(nameof(IsConnected));
            Error = "";
        }
        else if (connection.Detail is { Length: > 0 } detail)
        {
            Error = detail;
        }
    }

    private void ApplySessions(MobileHostConnection connection)
    {
        var remote = connection.Sessions;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in remote.OrderByDescending(value => value.LastActivityAt))
        {
            present.Add(session.SessionId);
            var row = Sessions.FirstOrDefault(value => value.Id == session.SessionId);
            if (row is null)
                Sessions.Add(new SessionRowViewModel(session));
            else
                row.Update(session);
        }
        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!present.Contains(Sessions[index].Id))
                Sessions.RemoveAt(index);
        }
    }

    private void ApplyPending(MobileHostConnection connection)
    {
        var approval = connection.Pending.Approvals.FirstOrDefault();
        HasApproval = approval is not null;
        _approvalRequestId = approval?.RequestId ?? "";
        // 待决列表是全局的: 标题里带上会话名, 避免在 A 会话里误批 B 会话的请求。
        ApprovalTitle = approval is null
            ? ""
            : $"{approval.Tool} 请求批准 · {SessionTitle(approval.SessionId)}";
        ApprovalDetail = approval is null
            ? ""
            : (approval.Reason is { Length: > 0 } reason ? reason : approval.Arguments ?? "");
        ApplyQuestions(connection.Pending.Questions.FirstOrDefault());
    }

    /** 只在 requestId 变化时重建题目, 否则用户已经勾好的选项会被待决广播刷掉。 */
    private void ApplyQuestions(RemoteQuestionRequest? request)
    {
        var id = request?.RequestId ?? "";
        if (id == QuestionRequestId)
            return;
        foreach (var question in Questions)
            question.Changed -= OnAnswerChanged;
        Questions.Clear();
        QuestionRequestId = id;
        if (request is null)
        {
            HasQuestions = false;
            QuestionTitle = "";
            return;
        }
        var session = SessionTitle(request.SessionId);
        QuestionTitle = request.Questions.Count == 1
            ? $"{request.Questions[0].Question} · {session}"
            : $"需要你回答 {request.Questions.Count} 个问题 · {session}";
        foreach (var item in request.Questions)
        {
            var question = new QuestionItemViewModel(item);
            question.Changed += OnAnswerChanged;
            Questions.Add(question);
        }
        HasQuestions = true;
        SubmitAnswersCommand.NotifyCanExecuteChanged();
    }

    private void OnAnswerChanged() => SubmitAnswersCommand.NotifyCanExecuteChanged();

    private bool CanSubmitAnswers()
        => !IsSendingAnswers
        && HasQuestions
        && Questions.All(question => question.SelectedLabels.Count > 0 || question.Custom.Trim().Length > 0);

    [RelayCommand(CanExecute = nameof(CanSubmitAnswers))]
    private async Task SubmitAnswersAsync()
    {
        if (_connection is null || QuestionRequestId.Length == 0)
            return;
        IsSendingAnswers = true;
        try
        {
            var answers = Questions
                .Select(question => new RemoteQuestionAnswerItem(
                    question.Id,
                    question.SelectedLabels,
                    question.Custom.Trim() is { Length: > 0 } custom ? custom : null))
                .ToList();
            var lease = await _connection.WaitConnectedAsync(_lifetime.Token);
            var accepted = await lease.Client.RespondQuestionAsync(QuestionRequestId, answers, _lifetime.Token);
            Error = accepted ? "" : "问题已被其他客户端回答";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = $"提交回答失败: {error.Message}";
        }
        finally
        {
            IsSendingAnswers = false;
        }
    }

    private string SessionTitle(string sessionId)
        => Sessions.FirstOrDefault(row => row.Id == sessionId)?.Title ?? sessionId;

    partial void OnSelectedSessionChanged(SessionRowViewModel? value)
    {
        if (value is null)
            return;
        _ = OpenSessionAsync(value);
    }

    private async Task OpenSessionAsync(SessionRowViewModel row)
    {
        await StopFollowingAsync();
        _openSessionId = row.Id;
        Page = MobilePage.Chat;
        OnPropertyChanged(nameof(ChatTitle));
        OnPropertyChanged(nameof(ChatSubtitle));
        if (row.Kind == HostSessionKinds.Terminal)
        {
            Error = "终端会话请在桌面端操作";
            return;
        }
        if (_connection is null)
            return;

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sessionLifetime = lifetime;
        try
        {
            await foreach (var item in _connection.SubscribeAsync(row.Id, 0, lifetime.Token))
            {
                if (item.Reset)
                {
                    await ReloadSnapshotAsync(row.Id, lifetime.Token);
                    continue;
                }
                _events.Enqueue(item);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() => Error = $"无法读取会话: {error.Message}");
        }
    }

    private async Task ReloadSnapshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_connection is null)
            return;
        RemoteSessionSnapshot snapshot;
        try
        {
            var lease = await _connection.WaitConnectedAsync(cancellationToken);
            snapshot = await lease.Client.GetSessionSnapshotAsync(sessionId, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => Error = $"无法读取会话快照: {error.Message}");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || sessionId != _openSessionId)
                return;
            _transcript = new SubagentTranscript();
            _renderedSeq = 0;
            Messages.Clear();
            ApplyEvents(snapshot.Events);
        });
    }

    /** 停止跟随后台订阅并清空消息; 不动 SelectedSession, 供"换一个会话"与"返回列表"共用。 */
    private async Task StopFollowingAsync()
    {
        var lifetime = _sessionLifetime;
        _sessionLifetime = null;
        if (lifetime is not null)
        {
            await lifetime.CancelAsync();
            lifetime.Dispose();
        }
        _openSessionId = "";
        _renderedSeq = 0;
        _transcript = new SubagentTranscript();
        Messages.Clear();
        Error = "";
        SendCommand.NotifyCanExecuteChanged();
    }

    private async Task CloseSessionAsync()
    {
        await StopFollowingAsync();
        SelectedSession = null;
        Page = _connection is null ? MobilePage.Connect : MobilePage.Sessions;
        OnPropertyChanged(nameof(ChatTitle));
        OnPropertyChanged(nameof(ChatSubtitle));
    }

    /** UI 线程上的批量落盘: 只在这一个地方改 Messages, 后台线程不碰绑定集合。 */
    private void ApplyEvents(IReadOnlyList<RemoteEventInfo> batch)
    {
        var changed = false;
        foreach (var item in batch)
        {
            if (item.Sequence < _renderedSeq)
                continue;
            _renderedSeq = item.Sequence + 1;
            if (Deserialize(item.EventJson) is { } sessionEvent)
            {
                _transcript.Apply(sessionEvent, Messages);
                changed = true;
            }
        }
        if (!changed)
            return;
        _transcript.Flush();
        var now = DateTime.UtcNow;
        if ((now - _lastScrollRequest).TotalMilliseconds < MessageScrollThrottleMilliseconds)
            return;
        _lastScrollRequest = now;
        TranscriptChanged?.Invoke();
    }

    private static SessionEvent? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, DshCoreJsonContext.Default.SessionEvent);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
