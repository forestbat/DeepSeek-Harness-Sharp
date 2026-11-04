using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Workspace;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/**
 * One session of one machine as the dashboard, the command palette and the session header show it.
 * The card is updated in place when the host reports a change, so lists keep their items (and the user's scroll position).
 */
public sealed partial class SessionCardViewModel(string machine, RemoteSessionSummary session) : ObservableObject
{
    public string Machine { get; } = machine;

    public string SessionId { get; } = session.SessionId;

    public RemoteSessionSummary Session { get; private set; } = session;

    /** The machine's connection state; sessions of an unreachable machine show their last known values, greyed out. */
    [ObservableProperty]
    private MachineConnectionState _machineState = MachineConnectionState.Connected;

    public string Kind => Session.Kind ?? HostSessionKinds.Harness;

    public string? AgentKind => Session.AgentKind;

    public string? ProjectPath => Session.ProjectPath;

    public string ProjectName => SessionPresentation.ProjectName(Session.ProjectPath);

    public string AgentLabel => SessionPresentation.AgentLabel(Session.AgentKind, Kind);

    public string KindLabel => SessionPresentation.KindLabel(Kind);

    public string BadgeGlyph => SessionPresentation.Badge(Session.AgentKind, Kind).Glyph;

    public IBrush BadgeBrush => SolidColorBrush.Parse(SessionPresentation.Badge(Session.AgentKind, Kind).Color);

    public string Title => string.IsNullOrWhiteSpace(Session.Title) ? AgentLabel : Session.Title;

    public string Status => Session.Status;

    public bool IsMachineOnline => MachineState == MachineConnectionState.Connected;

    /** While the machine is unreachable the card shows the connection state instead of the stale session status. */
    public string StatusLabel => IsMachineOnline ? SessionPresentation.StatusLabel(Session.Status) : SessionPresentation.MachineStateLabel(MachineState);

    public bool NeedsAttention => IsMachineOnline && SessionPresentation.NeedsAttention(Session);

    public bool IsRunning => IsMachineOnline && Session.Status == HostSessionStatuses.Running;

    public bool IsIdle => IsMachineOnline && Session.Status == HostSessionStatuses.Idle;

    public bool IsExited => Session.Status == HostSessionStatuses.Exited;

    public int PendingCount => Session.PendingCount;

    public bool HasPending => Session.PendingCount > 0;

    public string PendingLabel => $"{Session.PendingCount} 个待决";

    public DateTimeOffset LastActivityAt => DateTimeOffset.FromUnixTimeMilliseconds(Session.LastActivityAt);

    public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(Session.CreatedAt);

    public string LastActivityText => RelativeTimeText.Format(LastActivityAt);

    public string ExitLabel => Session.ExitCode is { } code ? $"退出码 {code}" : "";

    public bool CanStop => IsMachineOnline && !IsExited;

    /** Plain terminals have nothing to resume; agent and harness sessions start again on their machine. */
    public bool CanResume => IsMachineOnline && IsExited && Session.Kind != HostSessionKinds.Terminal;

    /** Text the quick switcher matches against: title, agent, project, machine and id. */
    public string SearchText => $"{Title} {AgentLabel} {Session.ProjectPath} {Machine} {SessionId}";

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameDraft = "";

    public SessionCardViewModel? RenameEditor => IsRenaming ? this : null;

    partial void OnIsRenamingChanged(bool value)
    {
        OnPropertyChanged(nameof(RenameEditor));
    }

    public void Update(RemoteSessionSummary session, MachineConnectionState machineState)
    {
        Session = session;
        MachineState = machineState;
        OnPropertyChanged(string.Empty);
    }

    public void RefreshActivityText() => OnPropertyChanged(nameof(LastActivityText));

    partial void OnMachineStateChanged(MachineConnectionState value) => OnPropertyChanged(string.Empty);
}
