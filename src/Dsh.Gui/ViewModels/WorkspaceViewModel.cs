using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Boot;
using Dsh.Workspace;
using Dsh.Gui.Services;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.ViewModels;

/** Where the workspace asks the main window to go. */
public enum WorkspaceDestination
{
    Dashboard,
    Session,
    Page,
}

/**
 * Root of the multi-machine workspace: the fleet connection, the dashboard, the notification center, the quick switcher and the session in view.
 * A session is shown by at most one view at a time (the main window or its own window), because a client attaches to a terminal or subscribes to a session once.
 * Contributed pages (files, Git, machine management) open through Pages with the machine and project of the session in view.
 */
public sealed partial class WorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dictionary<(string Machine, string Session), FleetSessionViewModel> _windows = [];
    private readonly Dictionary<FleetSessionViewModel, Task> _retirements = [];
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private Task? _disposal;
    private bool _closingSessions;
    private readonly Action<string, string, bool> _toast;
    private readonly Func<WorkspacePageContext>? _pageContext;
    private (string Machine, string SessionId)? _pendingOpen;

    public WorkspaceViewModel(
        FleetWorkspace fleet,
        Action<string, string, bool> toast,
        GuiSettings? gui = null,
        HarnessHome? home = null,
        Func<(string Machine, string SessionId)>? current = null,
        Func<WorkspacePageContext>? pageContext = null)
    {
        Fleet = fleet;
        _toast = toast;
        _pageContext = pageContext;
        Actions = new FleetSessionActions(fleet);
        SessionActions = new SessionActions(fleet, home ?? fleet.Services.LocalHome ?? new HarnessHome(Environment.CurrentDirectory), current);
        Dashboard = new DashboardViewModel(fleet, gui, SessionActions, current);
        Dashboard.ActionReported += toast;
        Notifications = new NotificationCenterViewModel(fleet, (machine, session) => Dashboard.Find(machine, session)?.Title) { Toast = toast };
        Palette = new CommandPaletteViewModel(() => Dashboard.AllCards);
        Machines = new MachinesViewModel(fleet.Services);
        Machines.Load();
        Machines.AttachFleet(fleet.Client);
        Machines.MachinesChanged += () => _ = ReloadMachinesAsync();
        Dashboard.OpenRequested += card => OpenSession(card.Machine, card.SessionId);
        Dashboard.OpenInWindowRequested += OpenInWindow;
        Dashboard.StopRequested += card => _ = StopAsync(card);
        Dashboard.ResumeRequested += card => _ = ResumeAsync(card);
        Dashboard.NewSessionRequested += NewSession;
        Notifications.OpenSessionRequested += OpenSession;
        Notifications.AnswerRequested += decision => DecisionRequested?.Invoke(decision) ?? Task.FromResult<object?>(null);
        Palette.OpenRequested += card => OpenSession(card.Machine, card.SessionId);
        Fleet.Changed += OnFleetChanged;
    }

    public FleetWorkspace Fleet { get; }

    public FleetSessionActions Actions { get; }

    public SessionActions SessionActions { get; }

    public DashboardViewModel Dashboard { get; }

    public NotificationCenterViewModel Notifications { get; }

    public CommandPaletteViewModel Palette { get; }

    public WorkspacePages Pages { get; } = new();

    /** Opens the folder picker used by path-based host pages (Git). */
    public Func<Task<string?>>? FolderPicker { get; set; }
    public Func<string, string, EditorOpenWithViewModel>? CreateEditorPicker { get; set; }

    private string? GitWorkspaceMachine { get; set; }

    private string? GitWorkspacePath { get; set; }

    /** Machine management, shared by its workspace page and the machine page navigation. */
    public MachinesViewModel Machines { get; }

    [ObservableProperty]
    private FleetSessionViewModel? _activeSession;

    /** Content of the contributed page in view (see WorkspacePageEntry); a disposable content is disposed when the page closes. */
    [ObservableProperty]
    private object? _pageContent;

    partial void OnPageContentChanged(object? oldValue, object? newValue)
    {
        if (oldValue is IDisposable disposable)
            disposable.Dispose();
    }

    [ObservableProperty]
    private WorkspacePageEntry? _activePage;

    public event Action<WorkspaceDestination>? NavigationRequested;

    /** The view opens a window for the session and calls SessionWindowClosed when it closes. */
    public event Action<FleetSessionViewModel>? SessionWindowRequested;

    /** The session is already shown in its own window; the view brings that window to the front. */
    public event Action<FleetSessionViewModel>? SessionWindowFocusRequested;

    /** The machine list changed under the session's window; the view closes it and calls SessionWindowClosedAsync. */
    public event Action<FleetSessionViewModel>? SessionWindowCloseRequested;

    /** The view shows the new-session dialog for the form. */
    public event Action<NewSessionViewModel>? NewSessionDialogRequested;

    /** Questions are answered in the decision dialog that the main window shows. */
    public event Func<DecisionViewModel, Task<object?>>? DecisionRequested;

    /** The main chat shell handles harness/ACP cards on any machine with its full composer and trace UI. */
    public event Action<string, string>? ChatRequested;

    public void Start() => Fleet.Start();

    /** Shows a session in the main window, unless its own window already shows it. */
    public void OpenSession(string machine, string sessionId)
    {
        _pendingOpen = null;
        if (_closingSessions || _disposal is not null)
            return;
        if (_windows.TryGetValue((machine, sessionId), out var windowed))
        {
            SessionWindowFocusRequested?.Invoke(windowed);
            return;
        }
        if (ActiveSession is { } active && active.Machine == machine && active.SessionId == sessionId)
        {
            NavigationRequested?.Invoke(WorkspaceDestination.Session);
            return;
        }
        if (Dashboard.Find(machine, sessionId) is not { } card)
        {
            _toast("会话不存在", $"{machine} 上没有会话 {sessionId}", true);
            return;
        }
        ShowInMain(card);
    }

    /** Moves the session into its own window; when the main window shows it, the same view moves over without attaching again. */
    public void OpenInWindow(SessionCardViewModel card)
    {
        if (_closingSessions || _disposal is not null)
            return;
        if (card.Kind is HostSessionKinds.Harness or HostSessionKinds.Acp)
        {
            ShowInMain(card);
            return;
        }
        var key = (card.Machine, card.SessionId);
        if (_windows.TryGetValue(key, out var existing))
        {
            SessionWindowFocusRequested?.Invoke(existing);
            return;
        }
        var session = ActiveSession is { } active && active.Machine == card.Machine && active.SessionId == card.SessionId ? active : CreateSession(card);
        if (ReferenceEquals(session, ActiveSession))
        {
            ActiveSession = null;
            NavigationRequested?.Invoke(WorkspaceDestination.Dashboard);
        }
        session.IsInWindow = true;
        _windows[key] = session;
        SessionWindowRequested?.Invoke(session);
    }

    public Task SessionWindowClosedAsync(FleetSessionViewModel session)
    {
        var retirement = RetireSession(session);
        if (_windows.GetValueOrDefault((session.Machine, session.SessionId)) == session)
            _windows.Remove((session.Machine, session.SessionId));
        return retirement;
    }

    /** Back to the dashboard; the session view is closed, the dashboard keeps its filter, search and scroll position. */
    [RelayCommand]
    public void ShowDashboard()
    {
        _pendingOpen = null;
        CloseActive();
        PageContent = null;
        ActivePage = null;
        NavigationRequested?.Invoke(WorkspaceDestination.Dashboard);
    }

    [RelayCommand]
    public void OpenPage(WorkspacePageEntry? entry)
    {
        if (entry is null)
            return;
        _pendingOpen = null;
        var context = _pageContext?.Invoke() ?? new WorkspacePageContext(ActiveSession?.Machine, ActiveSession?.Card.ProjectPath);
        if (entry.Id == WorkspacePages.Git && GitWorkspacePath is { Length: > 0 } explicitGitPath)
            context = new(GitWorkspaceMachine ?? context.Machine, explicitGitPath);
        PageContent = entry.Create(context);
        ActivePage = entry;
        NavigationRequested?.Invoke(WorkspaceDestination.Page);
    }

    /**
     * The pages this module contributes: files and Git on the machine and project of the session in view (this computer's home directory from the dashboard), and machine management.
     * Files and Git need the host capabilities fs.transfer and git.inspect; a new session asked for from a Git worktree opens the new-session form in that worktree.
     */
    public void RegisterHostPages(Geometry filesIcon, Geometry gitIcon, Geometry machinesIcon)
    {
        Pages.Register(new WorkspacePageEntry(WorkspacePages.Files, "文件", filesIcon, context => new HostPageViewModel(
            Fleet, "文件", FilesProtocol.Capability, context.Machine ?? MachineSpec.LocalName, context.ProjectPath ?? RemotePaths.Home,
            async (host, machine, path, style) =>
            {
                var files = new FilesViewModel(host.Files, path, style);
                if (await files.LoadAsync())
                {
                    files.OpenWith = CreateEditorPicker?.Invoke(machine, files.Roots[0].Path);
                    if (files.OpenWith is not null) await files.OpenWith.RefreshAsync();
                }
                return files;
            })));
        Pages.Register(new WorkspacePageEntry(WorkspacePages.Git, "Git", gitIcon, context => new HostPageViewModel(
            Fleet, "Git", GitProtocol.Capability, context.Machine ?? MachineSpec.LocalName, context.ProjectPath ?? "",
            async (host, machine, path, style) =>
            {
                var git = new GitViewModel(host.Git, host.Files, path, style);
                git.NewSessionRequested += worktree => NewSession(machine, worktree.Path);
                await git.RefreshAsync();
                return git;
            },
            folderPicker: () => FolderPicker?.Invoke() ?? Task.FromResult<string?>(null),
            pathSelected: (machine, path) => (GitWorkspaceMachine, GitWorkspacePath) = (machine, path))));
        Pages.Register(new WorkspacePageEntry(WorkspacePages.Machines, "机器", machinesIcon, _ => Machines));
    }

    /** Opens the new-session form, preset to a machine and project when started from one. */
    public void NewSession(string? machine, string? projectPath)
    {
        var form = new NewSessionViewModel(Fleet, Actions, machine, projectPath);
        form.Completed += OnNewSessionCompleted;
        NewSessionDialogRequested?.Invoke(form);
    }

    /**
     * Reconnects to the saved machine list; session views hold the previous connections, so they are closed first.
     * A contributed page in view stays: host pages follow their machine's new connection, and machine management shows the new client's states.
     */
    public async Task ReloadMachinesAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _closingSessions = true;
            if (ActivePage is not null)
                CloseActive();
            else
                ShowDashboard();
            await CloseSessionsAsync();
            await Fleet.ReloadAsync();
            Machines.AttachFleet(Fleet.Client);
        }
        finally
        {
            _closingSessions = false;
            _connectionGate.Release();
        }
    }

    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        Machines.BeginNetworkShutdown();
        await Machines.WaitForNetworkOperationsAsync();
        await _connectionGate.WaitAsync();
        try
        {
            _closingSessions = true;
            Fleet.Changed -= OnFleetChanged;
            PageContent = null;
            Machines.AttachFleet(null);
            CloseActive();
            await CloseSessionsAsync();
            Notifications.Dispose();
            Dashboard.Dispose();
            await Fleet.DisposeAsync();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private void ShowInMain(SessionCardViewModel card)
    {
        CloseActive();
        if (card.Kind is HostSessionKinds.Harness or HostSessionKinds.Acp && ChatRequested is { } openChat)
        {
            PageContent = null;
            ActivePage = null;
            openChat(card.Machine, card.SessionId);
            return;
        }
        ActiveSession = CreateSession(card);
        PageContent = null;
        ActivePage = null;
        NavigationRequested?.Invoke(WorkspaceDestination.Session);
    }

    private FleetSessionViewModel CreateSession(SessionCardViewModel card)
    {
            var session = new FleetSessionViewModel(Fleet, card, Notifications);
        session.BackRequested += _ => ShowDashboard();
        session.StopRequested += item => _ = StopAsync(item);
        session.ResumeRequested += item => _ = ResumeAsync(item);
        session.OpenInWindowRequested += OpenInWindow;
        return session;
    }

    private void CloseActive()
    {
        if (ActiveSession is not { } active)
            return;
        RetireSession(active);
        ActiveSession = null;
    }

    /** Navigation releases the view immediately, but its resources stay owned until the connection can retire. */
    private Task RetireSession(FleetSessionViewModel session)
    {
        foreach (var (completed, task) in _retirements)
            if (task.IsCompletedSuccessfully)
                _retirements.Remove(completed);
        if (!_retirements.TryGetValue(session, out var retirement))
        {
            retirement = session.DisposeAsync().AsTask();
            _retirements.Add(session, retirement);
        }
        return retirement;
    }

    private async Task CloseSessionsAsync()
    {
        foreach (var session in _windows.Values.ToList())
        {
            _ = RetireSession(session);
            SessionWindowCloseRequested?.Invoke(session);
        }
        _windows.Clear();
        await Task.WhenAll(_retirements.Values);
        _retirements.Clear();
    }

    private async Task StopAsync(SessionCardViewModel card)
    {
        try
        {
            await Actions.StopAsync(card.Machine, card.SessionId, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or OperationCanceledException)
        {
            _toast($"停止失败 · {card.Title}", error.Message, true);
        }
    }

    /** A terminal-mode agent comes back under a new session id; the view follows it when it showed the old one. */
    private async Task ResumeAsync(SessionCardViewModel card)
    {
        try
        {
            var resumed = await Actions.ResumeAsync(card.Machine, card.Session, CancellationToken.None);
            if (resumed != card.SessionId && ActiveSession?.SessionId == card.SessionId)
                await OpenWhenListedAsync(card.Machine, resumed);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or OperationCanceledException)
        {
            _toast($"恢复失败 · {card.Title}", error.Message, true);
        }
    }

    private void OnNewSessionCompleted(NewSessionViewModel form)
    {
        form.Completed -= OnNewSessionCompleted;
        if (form.Machine is { } machine && form.Started is { } started)
            _ = OpenWhenListedAsync(machine, started.SessionId);
    }

    /** Navigation waits for the Host registry projection instead of adding a speculative local session. */
    private async Task OpenWhenListedAsync(string machine, string sessionId)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _pendingOpen = (machine, sessionId);
            Dashboard.Refresh();
            OpenPendingSession();
        });
    }

    private void OpenPendingSession()
    {
        if (_pendingOpen is { } pending && Dashboard.Find(pending.Machine, pending.SessionId) is not null)
            OpenSession(pending.Machine, pending.SessionId);
    }

    private void OnFleetChanged()
    {
        if (Palette.IsOpen)
            Palette.Search();
        OpenPendingSession();
    }
}
