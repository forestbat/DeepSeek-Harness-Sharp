using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.Terminal;

namespace Dsh.Gui.ViewModels;

/**
 * The view of one terminal session on any machine: a header and the PTY, which other clients may share.
 * Harness and ACP sessions use the main ChatView instead.
 */
public sealed partial class FleetSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly FleetWorkspace _fleet;
    private readonly MachineConnection _machine;
    private readonly NotificationCenterViewModel _notifications;
    private readonly List<Task> _tabClosures = [];
    private readonly CancellationTokenSource _retirement = new();
    private Task? _disposal;

    public FleetSessionViewModel(FleetWorkspace fleet, SessionCardViewModel card, NotificationCenterViewModel notifications)
    {
        _fleet = fleet;
        _notifications = notifications;
        _machine = fleet.Find(card.Machine) ?? throw new InvalidOperationException($"no machine named \"{card.Machine}\"");
        Card = card;
        Tabs.Add(CreateSessionTab());
        SelectTab(Tabs[0]);
        _fleet.Changed += RefreshPending;
        RefreshPending();
    }

    public SessionCardViewModel Card { get; }

    public string Machine => Card.Machine;

    public string SessionId => Card.SessionId;

    public string ProjectLabel => Card.ProjectPath ?? "未指定项目目录";

    public ObservableCollection<SessionTabViewModel> Tabs { get; } = [];

    /** This session's approvals and questions, answerable in the header; the notification center lists the same items. */
    public ObservableCollection<PendingDecisionViewModel> Pending { get; } = [];

    [ObservableProperty]
    private SessionTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _error = "";

    /** Shown in its own window rather than the main window, which has no dashboard to go back to. */
    [ObservableProperty]
    private bool _isInWindow;

    public bool HasPending => Pending.Count > 0;

    public bool HasError => Error.Length > 0;

    public event Action<FleetSessionViewModel>? BackRequested;

    public event Action<SessionCardViewModel>? StopRequested;

    public event Action<SessionCardViewModel>? ResumeRequested;

    public event Action<SessionCardViewModel>? OpenInWindowRequested;

    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _fleet.Changed -= RefreshPending;
        try
        {
            await _retirement.CancelAsync();
        }
        finally
        {
            try
            {
                foreach (var tab in Tabs)
                    _tabClosures.Add(tab.CloseAsync().AsTask());
                await Task.WhenAll(_tabClosures);
                Tabs.Clear();
                _tabClosures.Clear();
            }
            finally
            {
                _retirement.Dispose();
            }
        }
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void SelectTab(SessionTabViewModel? tab)
    {
        if (tab is null)
            return;
        foreach (var item in Tabs)
            item.IsSelected = ReferenceEquals(item, tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private async Task CloseTabAsync(SessionTabViewModel? tab)
    {
        if (_disposal is not null || tab is not { IsClosable: true } || !Tabs.Contains(tab))
            return;
        var index = Tabs.IndexOf(tab);
        var closing = tab.CloseAsync().AsTask();
        _tabClosures.RemoveAll(task => task.IsCompletedSuccessfully);
        _tabClosures.Add(closing);
        Tabs.Remove(tab);
        if (tab.IsSelected)
            SelectTab(Tabs[Math.Max(0, index - 1)]);
        await closing;
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsInWindow)
            BackRequested?.Invoke(this);
    }

    [RelayCommand]
    private void Stop() => StopRequested?.Invoke(Card);

    [RelayCommand]
    private void Resume() => ResumeRequested?.Invoke(Card);

    [RelayCommand]
    private void OpenInWindow() => OpenInWindowRequested?.Invoke(Card);

    private SessionTabViewModel CreateSessionTab()
    {
        var terminal = new TerminalViewModel(Card.Title);
        terminal.AttachRemote(RemotePtyConnection.Attach(_machine, SessionId), $"{Machine} · {Card.AgentLabel}");
        return SessionTabViewModel.ForSessionTerminal(terminal);
    }

    private void RefreshPending()
    {
        CollectionSync.Sync(Pending, _notifications.PendingOf(Machine, SessionId));
        OnPropertyChanged(nameof(HasPending));
    }
}
