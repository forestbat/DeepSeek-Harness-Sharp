using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Workspace;

namespace Dsh.Gui.ViewModels;

/** One machine on the dashboard: its connection state and host release, then its projects. */
public sealed partial class DashboardMachineViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    public ObservableCollection<DashboardProjectViewModel> Projects { get; } = [];

    [ObservableProperty]
    private string _target = "";

    [ObservableProperty]
    private MachineConnectionState _state = MachineConnectionState.Connecting;

    [ObservableProperty]
    private string? _hostVersion;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private int _sessionCount;

    [ObservableProperty]
    private bool _isExpanded = true;

    public string StateLabel => SessionPresentation.MachineStateLabel(State);

    public bool IsConnected => State == MachineConnectionState.Connected;

    public bool IsUnreachable => State is MachineConnectionState.Offline or MachineConnectionState.Reconnecting;

    public bool IsBusy => State is MachineConnectionState.Connecting or MachineConnectionState.Deploying;

    public string VersionLabel => HostVersion is { Length: > 0 } version ? $"host {version}" : "";

    public bool HasDetail => !IsConnected && Detail is { Length: > 0 };

    public bool HasNoVisibleSessions => Projects.Count == 0;

    public void Apply(FleetMachineStatus status)
    {
        Target = status.Target;
        State = status.State;
        HostVersion = status.HostVersion;
        Detail = status.Detail;
    }

    public void NotifyProjectsChanged() => OnPropertyChanged(nameof(HasNoVisibleSessions));

    partial void OnStateChanged(MachineConnectionState value)
    {
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsUnreachable));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasDetail));
    }

    partial void OnHostVersionChanged(string? value) => OnPropertyChanged(nameof(VersionLabel));

    partial void OnDetailChanged(string? value) => OnPropertyChanged(nameof(HasDetail));

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/** The sessions of one project directory on one machine, shown side by side. */
public sealed partial class DashboardProjectViewModel(string machine, string? projectPath) : ObservableObject
{
    public string Machine { get; } = machine;

    public string? ProjectPath { get; } = projectPath;

    public string Name => SessionPresentation.ProjectName(ProjectPath);

    public string PathLabel => ProjectPath ?? "";

    public ObservableCollection<SessionCardViewModel> Sessions { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
