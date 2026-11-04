using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Workspace;
using Dsh.Gui.Services;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.ViewModels;

public enum DashboardFilter
{
    All,
    Attention,
    Running,
    Idle,
    Exited,
}

public enum DashboardSort
{
    Activity,
    Created,
    Title,
    Status,
}

/** A selectable chip (filter or sort order) of the dashboard toolbar. */
public sealed partial class DashboardChipViewModel(string label, int value) : ObservableObject
{
    public string Label { get; } = label;

    public int Value { get; } = value;

    [ObservableProperty]
    private bool _isSelected;
}

/**
 * The start page: every session of every machine, grouped machine → project → session, with status filters, keyword search and sort orders.
 * It owns one card per session (shared with the command palette and the session header) and keeps the cards and groups in place across updates.
 * Filter, search and sort live here, so leaving for a session and coming back keeps them.
 */
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    /** Relative activity times ("3 分钟前") are re-rendered at this pace. */
    private static readonly TimeSpan ActivityClock = TimeSpan.FromSeconds(30);

    private readonly FleetWorkspace _fleet;
    private readonly GuiSettings? _gui;
    private readonly SessionActions? _actions;
    private readonly Func<(string Machine, string SessionId)>? _current;
    private readonly Dictionary<(string Machine, string Session), SessionCardViewModel> _cards = [];
    private readonly Dictionary<string, DashboardMachineViewModel> _machines = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Machine, string? Project), DashboardProjectViewModel> _projects = [];
    private readonly DispatcherTimer _clock;

    private bool _suppressLayoutSave;

    public DashboardViewModel(
        FleetWorkspace fleet,
        GuiSettings? gui = null,
        SessionActions? actions = null,
        Func<(string Machine, string SessionId)>? current = null)
    {
        _fleet = fleet;
        _gui = gui;
        _actions = actions;
        _current = current;
        _suppressLayoutSave = true;
        IsDetailsLayout = gui?.Load().DashboardLayout == GuiSettings.LayoutDetails;
        _suppressLayoutSave = false;
        FilterChips =
        [
            new("全部", (int)DashboardFilter.All) { IsSelected = true },
            new("需要关注", (int)DashboardFilter.Attention),
            new("运行中", (int)DashboardFilter.Running),
            new("空闲", (int)DashboardFilter.Idle),
            new("已退出", (int)DashboardFilter.Exited),
        ];
        SortChips =
        [
            new("最近活动", (int)DashboardSort.Activity) { IsSelected = true },
            new("创建时间", (int)DashboardSort.Created),
            new("标题", (int)DashboardSort.Title),
            new("状态", (int)DashboardSort.Status),
        ];
        _fleet.Changed += Refresh;
        _clock = new DispatcherTimer(ActivityClock, DispatcherPriority.Background, (_, _) => RefreshTimes());
        _clock.Start();
        Refresh();
    }

    public ObservableCollection<DashboardMachineViewModel> Machines { get; } = [];

    public ObservableCollection<SessionCardViewModel> RecentCards { get; } = [];

    public IReadOnlyList<DashboardChipViewModel> FilterChips { get; }

    public IReadOnlyList<DashboardChipViewModel> SortChips { get; }

    /** Every card of every machine, in machine then project then activity order (the command palette searches these). */
    public IReadOnlyList<SessionCardViewModel> AllCards { get; private set; } = [];

    [ObservableProperty]
    private DashboardFilter _filter = DashboardFilter.All;

    [ObservableProperty]
    private DashboardSort _sort = DashboardSort.Activity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardsLayout))]
    [NotifyPropertyChangedFor(nameof(LayoutLabel))]
    private bool _isDetailsLayout;

    public bool IsCardsLayout => !IsDetailsLayout;

    public string LayoutLabel => IsDetailsLayout ? "卡片视图" : "详细信息";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _runningCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttention))]
    private int _attentionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOfflineMachines))]
    private int _offlineMachineCount;

    [ObservableProperty]
    private int _sessionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoVisibleSessions))]
    private int _visibleCount;

    public bool HasAttention => AttentionCount > 0;

    public bool HasOfflineMachines => OfflineMachineCount > 0;

    public bool HasNoVisibleSessions => VisibleCount == 0;

    /** Raised when a card asks to be opened (click); the workspace navigates to its session view. */
    public event Action<SessionCardViewModel>? OpenRequested;

    public event Action<SessionCardViewModel>? OpenInWindowRequested;

    public event Action<SessionCardViewModel>? StopRequested;

    public event Action<SessionCardViewModel>? ResumeRequested;

    /** Raised with the machine and project the new session should default to (null when started from the toolbar). */
    public event Action<string?, string?>? NewSessionRequested;

    public event Action<string, string, bool>? ActionReported;

    public SessionCardViewModel? Find(string machine, string sessionId)
        => _cards.GetValueOrDefault((machine, sessionId));

    public void Dispose()
    {
        _fleet.Changed -= Refresh;
        _clock.Stop();
    }

    /** Re-reads the fleet's snapshots and brings cards, groups and counts up to date. */
    public void Refresh()
    {
        var states = _fleet.Client.Statuses().ToDictionary(status => status.Machine, StringComparer.Ordinal);
        AllCards = UpdateCards(states);
        RunningCount = AllCards.Count(card => card.IsRunning);
        AttentionCount = AllCards.Count(card => card.NeedsAttention);
        OfflineMachineCount = states.Values.Count(status => status.State is MachineConnectionState.Offline or MachineConnectionState.Reconnecting);
        SessionCount = AllCards.Count;
        var recentLimit = _gui?.Load().RecentSessionCount ?? 20;
        CollectionSync.Sync(RecentCards, [.. AllCards.OrderByDescending(card => card.LastActivityAt).Take(recentLimit)]);
        RebuildGroups(states);
    }

    partial void OnFilterChanged(DashboardFilter value)
    {
        foreach (var chip in FilterChips)
            chip.IsSelected = chip.Value == (int)value;
        Refresh();
    }

    partial void OnSortChanged(DashboardSort value)
    {
        foreach (var chip in SortChips)
            chip.IsSelected = chip.Value == (int)value;
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnIsDetailsLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCardsLayout));
        if (_suppressLayoutSave || _gui is null)
            return;
        _gui.Save(_gui.Load() with
        {
            DashboardLayout = value ? GuiSettings.LayoutDetails : GuiSettings.LayoutCards,
        });
    }

    [RelayCommand]
    private void SetLayout(string layout)
        => IsDetailsLayout = layout == GuiSettings.LayoutDetails;

    [RelayCommand]
    private void ToggleLayout() => IsDetailsLayout = !IsDetailsLayout;

    [RelayCommand]
    private void SelectFilter(DashboardChipViewModel? chip)
    {
        if (chip is not null)
            Filter = (DashboardFilter)chip.Value;
    }

    [RelayCommand]
    private void SelectSort(DashboardChipViewModel? chip)
    {
        if (chip is not null)
            Sort = (DashboardSort)chip.Value;
    }

    [RelayCommand]
    private void Open(SessionCardViewModel? card)
    {
        if (card is not null)
            OpenRequested?.Invoke(card);
    }

    [RelayCommand]
    private void OpenInWindow(SessionCardViewModel? card)
    {
        if (card is not null)
            OpenInWindowRequested?.Invoke(card);
    }

    [RelayCommand]
    private void BeginRename(SessionCardViewModel? card)
    {
        if (card is null)
            return;
        card.RenameDraft = card.Title;
        card.IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync(SessionCardViewModel? card)
    {
        if (card is null || !card.IsRenaming || _actions is null)
            return;
        card.IsRenaming = false;
        var title = card.RenameDraft.Trim();
        if (title.Length == 0 || title == card.Title)
            return;
        try
        {
            await _actions.RenameAsync(ToRef(card), title, CancellationToken.None);
            ActionReported?.Invoke("已重命名", title, false);
            Refresh();
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or OperationCanceledException)
        {
            ActionReported?.Invoke("重命名失败", error.Message, true);
        }
    }

    [RelayCommand]
    private void CancelRename(SessionCardViewModel? card)
    {
        if (card is not null)
            card.IsRenaming = false;
    }

    [RelayCommand]
    private async Task ExportLogAsync(SessionCardViewModel? card)
    {
        if (card is null || _actions is null)
            return;
        try
        {
            var path = await _actions.ExportLogAsync(ToRef(card), CancellationToken.None);
            ActionReported?.Invoke("已导出日志", path, false);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or OperationCanceledException)
        {
            ActionReported?.Invoke("导出失败", error.Message, true);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SessionCardViewModel? card)
    {
        if (card is null || _actions is null)
            return;
        try
        {
            await _actions.DeleteAsync(ToRef(card), CancellationToken.None);
            ActionReported?.Invoke("会话已删除", card.Title, false);
            Refresh();
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or OperationCanceledException)
        {
            ActionReported?.Invoke("删除失败", error.Message, true);
        }
    }

    [RelayCommand]
    private void Stop(SessionCardViewModel? card)
    {
        if (card is { CanStop: true })
            StopRequested?.Invoke(card);
    }

    [RelayCommand]
    private void Resume(SessionCardViewModel? card)
    {
        if (card is { CanResume: true })
            ResumeRequested?.Invoke(card);
    }

    [RelayCommand]
    private void NewSession() => NewSessionRequested?.Invoke(null, null);

    [RelayCommand]
    private void NewSessionOnMachine(DashboardMachineViewModel? machine) => NewSessionRequested?.Invoke(machine?.Name, null);

    [RelayCommand]
    private void NewSessionInProject(DashboardProjectViewModel? project) => NewSessionRequested?.Invoke(project?.Machine, project?.ProjectPath);

    [RelayCommand]
    private void ShowAttention() => Filter = DashboardFilter.Attention;

    private SessionRef ToRef(SessionCardViewModel card)
        => new(
            card.Machine,
            card.SessionId,
            card.Title,
            card.ProjectPath,
            card.Kind,
            card.IsCurrent,
            card.Session);

    private IReadOnlyList<SessionCardViewModel> UpdateCards(Dictionary<string, FleetMachineStatus> states)
    {
        var seen = new HashSet<(string, string)>();
        var cards = new List<SessionCardViewModel>();
        foreach (var (machine, session) in _fleet.Client.Sessions())
        {
            var state = states.TryGetValue(machine, out var status) ? status.State : MachineConnectionState.Offline;
            var key = (machine, session.SessionId);
            if (_cards.TryGetValue(key, out var card))
                card.Update(session, state);
            else
                _cards[key] = card = new SessionCardViewModel(machine, session) { MachineState = state };
            card.IsCurrent = _current?.Invoke() is { } current && current.Machine == machine && current.SessionId == session.SessionId;
            seen.Add(key);
            cards.Add(card);
        }
        foreach (var stale in _cards.Keys.Where(key => !seen.Contains(key)).ToList())
            _cards.Remove(stale);
        return cards;
    }

    private void RebuildGroups(Dictionary<string, FleetMachineStatus> states)
    {
        var visible = AllCards.Where(Matches).ToList();
        VisibleCount = visible.Count;
        var machines = new List<DashboardMachineViewModel>();
        foreach (var status in _fleet.Client.Statuses())
        {
            var machine = MachineGroup(status.Machine);
            machine.Apply(status);
            var mine = visible.Where(card => card.Machine == status.Machine).ToList();
            machine.SessionCount = AllCards.Count(card => card.Machine == status.Machine);
            CollectionSync.Sync(machine.Projects, ProjectsOf(status.Machine, mine));
            machine.NotifyProjectsChanged();
            machines.Add(machine);
        }
        CollectionSync.Sync(Machines, machines);
    }

    private List<DashboardProjectViewModel> ProjectsOf(string machine, IReadOnlyList<SessionCardViewModel> cards)
    {
        var projects = new List<DashboardProjectViewModel>();
        foreach (var group in cards.GroupBy(card => card.ProjectPath).OrderBy(group => group.Key ?? "", StringComparer.Ordinal))
        {
            var key = (machine, group.Key);
            if (!_projects.TryGetValue(key, out var project))
                _projects[key] = project = new DashboardProjectViewModel(machine, group.Key);
            CollectionSync.Sync(project.Sessions, [.. Ordered(group)]);
            projects.Add(project);
        }
        return projects;
    }

    private DashboardMachineViewModel MachineGroup(string name)
    {
        if (!_machines.TryGetValue(name, out var machine))
            _machines[name] = machine = new DashboardMachineViewModel(name);
        return machine;
    }

    private bool Matches(SessionCardViewModel card)
    {
        var search = SearchText.Trim();
        if (search.Length > 0 && !card.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase))
            return false;
        return Filter switch
        {
            DashboardFilter.Attention => card.NeedsAttention,
            DashboardFilter.Running => card.Status == HostSessionStatuses.Running,
            DashboardFilter.Idle => card.Status == HostSessionStatuses.Idle,
            DashboardFilter.Exited => card.IsExited,
            _ => true,
        };
    }

    private IEnumerable<SessionCardViewModel> Ordered(IEnumerable<SessionCardViewModel> cards) => Sort switch
    {
        DashboardSort.Created => cards.OrderByDescending(card => card.CreatedAt),
        DashboardSort.Title => cards.OrderBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
        DashboardSort.Status => cards.OrderBy(card => StatusRank(card.Status)).ThenByDescending(card => card.LastActivityAt),
        _ => cards.OrderByDescending(card => card.LastActivityAt),
    };

    /** Status order: what needs the user first, then what is busy, then what is quiet, then what has ended. */
    private static int StatusRank(string status) => status switch
    {
        HostSessionStatuses.WaitingForUser => 0,
        HostSessionStatuses.Running => 1,
        HostSessionStatuses.Idle => 2,
        _ => 3,
    };

    private void RefreshTimes()
    {
        foreach (var card in AllCards)
            card.RefreshActivityText();
    }
}
