using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Workspace;
using Dsh.Gui.Services;
using Dsh.Notifications;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.ViewModels;

/**
 * The bell: every machine's notification history (newest first) and every pending approval and question, answerable in place.
 * Histories and read marks live on the hosts, so marking read here reaches every other client through host.notify.changed, and their marks reach this one the same way.
 * New waiting, approval and question notifications also become desktop notifications while the window is in the background.
 */
public sealed partial class NotificationCenterViewModel : ObservableObject, IDisposable
{
    /** Beyond this the badge shows "99+". */
    private const int BadgeLimit = 99;

    private static readonly HashSet<NotificationKind> DesktopKinds = [NotificationKind.WaitingForInput, NotificationKind.ApprovalRequest, NotificationKind.Question];

    private readonly FleetWorkspace _fleet;
    private readonly Func<string, string, string?> _sessionTitle;
    private readonly List<NotificationItemViewModel> _all = [];
    private readonly Dictionary<(string Machine, string Request), PendingDecisionViewModel> _pending = [];

    public NotificationCenterViewModel(FleetWorkspace fleet, Func<string, string, string?> sessionTitle)
    {
        _fleet = fleet;
        _sessionTitle = sessionTitle;
        KindOptions = [new("全部类型", null), .. Enum.GetValues<NotificationKind>().Select(kind => new NotificationFilterOption(SessionPresentation.NotificationKindLabel(kind), kind.ToString()))];
        _selectedKind = KindOptions[0];
        _selectedMachine = new NotificationFilterOption("全部机器", null);
        _selectedSession = new NotificationFilterOption("全部会话", null);
        MachineOptions.Add(_selectedMachine);
        SessionOptions.Add(_selectedSession);
        _fleet.Changed += OnFleetChanged;
        _fleet.NotificationsChanged += OnNotificationsChanged;
        foreach (var machine in _fleet.Client.Machines)
            ReplaceHistory(machine);
        OnFleetChanged();
    }

    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    public ObservableCollection<PendingDecisionViewModel> Pending { get; } = [];

    public ObservableCollection<NotificationFilterOption> MachineOptions { get; } = [];

    public ObservableCollection<NotificationFilterOption> SessionOptions { get; } = [];

    public IReadOnlyList<NotificationFilterOption> KindOptions { get; }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private int _unreadTotal;

    [ObservableProperty]
    private NotificationFilterOption _selectedMachine;

    [ObservableProperty]
    private NotificationFilterOption _selectedKind;

    [ObservableProperty]
    private NotificationFilterOption _selectedSession;

    public bool HasUnread => UnreadTotal > 0;

    public string BadgeText => UnreadTotal > BadgeLimit ? $"{BadgeLimit}+" : UnreadTotal.ToString();

    public bool HasPending => Pending.Count > 0;

    public bool HasNoItems => Items.Count == 0;

    /** Whether the main window is in the foreground; desktop notifications are only sent when it is not. */
    public Func<bool>? IsForeground { get; set; }

    /** Sends a desktop notification (title, body); null where the platform has none. */
    public Action<string, string>? DesktopNotify { get; set; }

    /** Short transient messages (the existing toasts). */
    public Action<string, string, bool>? Toast { get; set; }

    public event Action<string, string>? OpenSessionRequested;

    public event Func<DecisionViewModel, Task<object?>>? AnswerRequested;

    /** Pending requests of one session, for its view's header. */
    public IReadOnlyList<PendingDecisionViewModel> PendingOf(string machine, string sessionId)
        => [.. Pending.Where(item => item.Machine == machine && item.SessionId == sessionId)];

    public void Dispose()
    {
        _fleet.Changed -= OnFleetChanged;
        _fleet.NotificationsChanged -= OnNotificationsChanged;
        foreach (var pending in _pending.Values)
            pending.Invalidate();
    }

    partial void OnUnreadTotalChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(BadgeText));
    }

    /** A drop-down whose items were replaced may push null back; that means no filter. */
    partial void OnSelectedMachineChanged(NotificationFilterOption value)
    {
        if (value is null)
            SelectedMachine = MachineOptions[0];
        else
            ApplyFilter();
    }

    partial void OnSelectedKindChanged(NotificationFilterOption value)
    {
        if (value is null)
            SelectedKind = KindOptions[0];
        else
            ApplyFilter();
    }

    partial void OnSelectedSessionChanged(NotificationFilterOption value)
    {
        if (value is null)
            SelectedSession = SessionOptions[0];
        else
            ApplyFilter();
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private Task MarkReadAsync(NotificationItemViewModel? item)
    {
        if (item is null || item.IsRead)
            return Task.CompletedTask;
        return SendMarkAsync(item.Machine, new RemoteNotifyMarkRead(Ids: [item.Id]));
    }

    /** Marks everything the current machine and session filters show (all machines when unfiltered). */
    [RelayCommand]
    private async Task MarkAllReadAsync()
    {
        var machines = SelectedMachine.Value is { } only ? [only] : _fleet.Client.Machines.Select(machine => machine.Machine.Name).ToList();
        foreach (var machine in machines)
            await SendMarkAsync(machine, new RemoteNotifyMarkRead(All: true, SessionId: SelectedSession.Value));
    }

    [RelayCommand]
    private async Task OpenAsync(NotificationItemViewModel? item)
    {
        if (item is null)
            return;
        IsOpen = false;
        if (item.SessionId is { } session)
            OpenSessionRequested?.Invoke(item.Machine, session);
        await MarkReadAsync(item);
    }

    private async Task SendMarkAsync(string machine, RemoteNotifyMarkRead request)
    {
        if (_fleet.Find(machine) is not { State: MachineConnectionState.Connected } connection)
            return;
        try
        {
            var lease = await connection.WaitConnectedAsync(CancellationToken.None);
            await lease.Client.Notifications.MarkNotificationsReadAsync(request, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or OperationCanceledException)
        {
            Toast?.Invoke($"{machine}: 标记已读失败", error.Message, true);
        }
    }

    private void ReplaceHistory(IWorkspaceMachine machine)
    {
        _all.RemoveAll(item => item.Machine == machine.Machine.Name);
        if (machine.Notifications is { } page)
            _all.AddRange(page.Items.Select(record => Describe(new NotificationItemViewModel(machine.Machine.Name, record))));
    }

    private void OnNotificationsChanged(IWorkspaceMachine machine, RemoteNotificationChange? change)
    {
        ReplaceHistory(machine);
        RefreshAll();
        if (change?.Added is { } record && DesktopKinds.Contains(record.Kind) && IsForeground?.Invoke() != true)
            DesktopNotify?.Invoke($"{machine.Machine.Name} · {record.Title}", record.Body ?? SessionPresentation.NotificationKindLabel(record.Kind));
    }

    private void OnFleetChanged()
    {
        var names = _fleet.Client.Machines.Select(machine => machine.Machine.Name).ToHashSet(StringComparer.Ordinal);
        _all.RemoveAll(item => !names.Contains(item.Machine));
        RefreshPending();
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshUnread();
        RefreshOptions();
        ApplyFilter();
    }

    private void RefreshUnread() => UnreadTotal = _fleet.Client.Machines.Sum(machine => machine.UnreadNotifications);

    private void RefreshPending()
    {
        var current = new List<PendingDecisionViewModel>();
        foreach (var item in _fleet.Client.Pending())
        {
            var key = (item.Machine, item.RequestId);
            if (!_pending.TryGetValue(key, out var pending))
                _pending[key] = pending = CreatePending(item);
            pending.SessionTitle = _sessionTitle(item.Machine, item.SessionId) ?? item.SessionId;
            pending.RefreshCapabilities();
            current.Add(pending);
        }
        foreach (var gone in _pending.Keys.Except(current.Select(item => (item.Machine, item.RequestId))).ToList())
        {
            _pending[gone].Invalidate();
            _pending.Remove(gone);
        }
        CollectionSync.Sync(Pending, current);
        OnPropertyChanged(nameof(HasPending));
    }

    private PendingDecisionViewModel CreatePending(FleetPendingItem item)
    {
        var pending = new PendingDecisionViewModel(item, () => _fleet.Find(item.Machine));
        pending.Superseded += _ => Toast?.Invoke("请求已被处理", "另一个客户端已先回答或请求已取消", false);
        pending.AnswerRequested += decision => AnswerRequested?.Invoke(decision) ?? Task.FromResult<object?>(null);
        pending.OpenRequested += request =>
        {
            IsOpen = false;
            OpenSessionRequested?.Invoke(request.Machine, request.SessionId);
        };
        return pending;
    }

    private NotificationItemViewModel Describe(NotificationItemViewModel item)
    {
        if (item.SessionId is { } session)
            item.SessionTitle = _sessionTitle(item.Machine, session) ?? session;
        return item;
    }

    private void RefreshOptions()
    {
        CollectionSync.Sync(MachineOptions, Options(MachineOptions, "全部机器", _fleet.Client.Machines.Select(machine => (machine.Machine.Name, machine.Machine.Name))));
        var sessions = _all.Where(item => item.SessionId is not null).DistinctBy(item => item.SessionId).Select(item => (Label: item.SessionTitle, Value: item.SessionId!));
        CollectionSync.Sync(SessionOptions, Options(SessionOptions, "全部会话", sessions));
    }

    /** The "everything" entry first, then one entry per value; entries that already exist are kept so the drop-down's selection survives. */
    private static List<NotificationFilterOption> Options(IEnumerable<NotificationFilterOption> existing, string everything, IEnumerable<(string Label, string Value)> values)
    {
        var known = existing.ToList();
        var result = new List<NotificationFilterOption> { known.FirstOrDefault(option => option.Value is null) ?? new NotificationFilterOption(everything, null) };
        foreach (var (label, value) in values.OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase))
            result.Add(known.FirstOrDefault(option => option.Value == value && option.Label == label) ?? new NotificationFilterOption(label, value));
        return result;
    }

    private void ApplyFilter()
    {
        var kind = SelectedKind.Value;
        var visible = _all
            .Where(item => SelectedMachine.Value is null || item.Machine == SelectedMachine.Value)
            .Where(item => SelectedSession.Value is null || item.SessionId == SelectedSession.Value)
            .Where(item => kind is null || item.Kind.ToString() == kind)
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
        CollectionSync.Sync(Items, visible);
        OnPropertyChanged(nameof(HasNoItems));
    }
}
