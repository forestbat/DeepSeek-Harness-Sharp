using Dsh.Boot;
using Dsh.RemoteHost;

namespace Dsh.Workspace;

/** A borrowed connection. Its lifetime ends before a replacement can be published. */
public readonly record struct WorkspaceHostLease(IWorkspaceHost Client, CancellationToken Lifetime, Task Closed);

public interface IWorkspaceMachine : IAsyncDisposable
{
    MachineSpec Machine { get; }
    MachineConnectionState State { get; }
    string? Detail { get; }
    HostInfo? Info { get; }
    HostHelloResponse? Hello { get; }
    IReadOnlyList<RemoteSessionSummary> Sessions { get; }
    RemotePendingList Pending { get; }
    RemoteNotifyPage? Notifications { get; }
    int UnreadNotifications { get; }
    IWorkspaceMachineNetwork Network { get; }
    event Action<IWorkspaceMachine>? StateChanged;
    event Action<IWorkspaceMachine>? SessionsChanged;
    event Action<IWorkspaceMachine>? PendingChanged;
    event Action<IWorkspaceMachine, RemoteNotificationChange?>? NotificationsChanged;
    void Start();
    Task WaitSettledAsync(CancellationToken cancellationToken);
    Task<WorkspaceHostLease> WaitConnectedAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken);
    Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken,
        Func<IWorkspaceHost, CancellationToken, Task>? prepare = null);
}

/** One independently disposable workspace's machine connections and authoritative projections. */
public interface IWorkspaceClient : IAsyncDisposable
{
    /** Completes after all owned connections and outbound leases have retired; observing never starts disposal. */
    Task WhenDisposed { get; }
    event Action? Changed;
    IReadOnlyList<IWorkspaceMachine> Machines { get; }
    IWorkspaceMachine? Find(string name);
    void Start();
    Task WaitSettledAsync(CancellationToken cancellationToken);
    IReadOnlyList<FleetMachineStatus> Statuses();
    IReadOnlyList<FleetSession> Sessions();
    IReadOnlyList<FleetMachineGroup> Groups();
    IReadOnlyList<FleetPendingItem> Pending();
}

public interface IWorkspaceMachineNetwork
{
    Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken = default);
    Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default);
    Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default, Action<string>? probeReady = null);
    Task ReloadLocalAsync(string expectedHome, CancellationToken cancellationToken);
}

public interface IMachineCatalog
{
    string FilePath { get; }
    FleetConfig Load();
    void Save(FleetConfig config);
    bool Remove(string name);
    void Validate(IReadOnlyList<MachineSpec> machines);
}

public sealed record MachineConnectionTest(MachineConnectionState State, HostInfo? Info, string? Detail, int SessionCount);
public sealed record WorkspaceDeployment(string CommandPath, bool AlreadyPresent);

/** Explicit user-initiated administration, independent of any active workspace connection. */
public interface IMachineAdministration
{
    bool IrohAvailable { get; }
    string? IrohExecutable { get; }
    Task<MachineConnectionTest> TestConnectionAsync(MachineSpec machine, CancellationToken cancellationToken);
    Task<WorkspaceDeployment> DeployAsync(MachineSpec machine, DeploySettings deploy, bool force, Action<string> progress, CancellationToken cancellationToken);
    Task<MachineSpec> EnableIrohAsync(MachineSpec machine, DeploySettings deploy, Action<MachineSpec> persist, Action<string> progress, CancellationToken cancellationToken);
    Task<IReadOnlyList<DoctorCheck>> DiagnoseAsync(MachineSpec machine, CancellationToken cancellationToken);
}

public interface IWorkspaceProxyProbe
{
    Task<ProxyProbeResult> ProbeSshAsync(MachineSpec machine, Guid operationId, CancellationToken cancellationToken);
    Task<ProxyProbeResult> ProbeLocalAsync(string home, OutboundProxySettings? settings, Guid operationId, CancellationToken cancellationToken);
    Task<ProxyProbeResult> ProbeRemoteAsync(IWorkspaceMachine connection, RemoteOutboundProxyProbeRequest request, Guid operationId, CancellationToken cancellationToken);
}

/** The configured backend supplies factories; the GUI owns the clients it creates. */
public interface IWorkspaceServices
{
    HarnessHome? LocalHome { get; }
    IMachineCatalog Store { get; }
    IMachineAdministration Administration { get; }
    IWorkspaceClient CreateClient();
    IWorkspaceProxyProbe CreateProxyProbe();
    Task SelectBorrowedOutboundAsync(string? machine, CancellationToken cancellationToken);
    Task<ProxyProbeResult> ProbeBorrowedOutboundAsync(string machine, Guid operationId, CancellationToken cancellationToken);
}

public interface IWorkspaceProvider : IDisposable
{
    const string ServiceName = "workspaceProvider";
    IWorkspaceServices Create(HarnessHome home, string clientName, Func<string, CancellationToken, Task>? startLocalHost = null);
}
