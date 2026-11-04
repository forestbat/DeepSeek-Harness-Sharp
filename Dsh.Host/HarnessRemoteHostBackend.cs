using Dsh.Boot;
using Dsh.Core;
using Dsh.Ide.Bridge;
using Dsh.Plugins;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Host;

/** Composes remote domains and owns their shared harness/daemon lifecycle. */
public sealed class HarnessRemoteHostBackend : IRemoteHostBackend, IAsyncDisposable
{
    private const string RegistryFileName = "sessions.json";

    private readonly HostInfo _info;
    private readonly HarnessRemoteRuntime _runtime;
    private readonly HostPtySessions _ptys;
    private readonly ExternalAgentSessions _agents;
    private readonly HarnessDecisionBridge _decisions;
    private readonly HarnessSessionTracker _tracker;

    private HarnessRemoteHostBackend(HarnessApp app, AcpSessionServices services, HostPtySessions ptys, ExternalAgentSessions agents)
    {
        Pending = services.Pending;
        Sessions = services.Registry;
        Notifications = services.Notifications;
        Files = new HostFileSystem(HostPathPolicy.ForHome(Sessions, app.Home.Root));
        Git = app.Ctx.Get<IGitService>(IGitService.ServiceName, false) is { } git
            ? new HostGitService(Files.Policy, git)
            : null;
        _ptys = ptys;
        _agents = agents;
        _decisions = new HarnessDecisionBridge(app.Ctx, Pending);
        _tracker = new HarnessSessionTracker(app.Ctx, Sessions, Pending);
        _runtime = new HarnessRemoteRuntime(app, Sessions, Pending, _tracker);
        Session = new HarnessRemoteSessionBackend(_runtime, agents);
        Agent = new HarnessRemoteAgentBackend(_runtime, Session, ptys, agents);
        Pty = new HarnessRemotePtyBackend(ptys);
        SessionEditing = new HarnessRemoteSessionEditingBackend(_runtime, Agent);
        if (app.Ctx.Get<IPluginManager>("pluginManager", false) is { } plugins)
            Plugins = new HarnessRemotePluginsBackend(app, plugins);
        if (app.Ctx.GetProp(OutboundProxy.PropName) is OutboundProxy proxy)
            Network = new HarnessRemoteNetworkBackend(app.Home, proxy, app.Ctx.Get<IOutboundRouteController>(IOutboundRouteController.ServiceName, false));
        if (app.Ctx.Get<EditorExecutionContext>(EditorExecutionContext.ServiceName, false) is { } editorContext)
            editorContext.IsRemoteHost = true;
        if (app.Ctx.Get<IEditorService>(IEditorService.ServiceName, false) is { } editor)
            Editor = new HarnessRemoteEditorBackend(editor, app.Ctx.Get<EditorClientDispatcher>(EditorClientDispatcher.ServiceName, false), Files.Policy);
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        _info = new HostInfo(HostProtocol.Version, HostProtocol.ReleaseVersion, platform, app.Home.Root);
    }

    /** Restore the registry and reconcile the daemon before exposing any domain. */
    public static async Task<HarnessRemoteHostBackend> CreateAsync(HarnessApp app, HostPtyDaemon? ptyDaemon = null, CancellationToken cancellationToken = default)
    {
        var pending = new HostPendingDecisions();
        var sessions = await HostSessionRegistry.OpenAsync(Path.Combine(app.Home.HostPath, RegistryFileName), pending, cancellationToken).ConfigureAwait(false);
        var notifications = await HostNotificationCenter.OpenAsync(Path.Combine(app.Home.HostPath, HostNotificationCenter.FileName), sessions, pending, cancellationToken).ConfigureAwait(false);
        var services = new AcpSessionServices(sessions, pending, notifications);
        var ptys = new HostPtySessions(sessions, ptyDaemon ?? HostPtyDaemon.Default(), app.Home.Root);
        await ptys.RestoreAsync(cancellationToken).ConfigureAwait(false);
        var agents = new ExternalAgentSessions(app.Ctx, services, ptys);
        return new HarnessRemoteHostBackend(app, services, ptys, agents);
    }

    public HostPendingDecisions Pending { get; }
    public HostSessionRegistry Sessions { get; }
    public IRemoteSessionBackend Session { get; }
    public IRemoteAgentBackend Agent { get; }
    public IRemotePtyBackend Pty { get; }
    public HostFileSystem Files { get; }
    public HostGitService? Git { get; }
    public HostNotificationCenter Notifications { get; }
    public IRemoteSessionEditingBackend SessionEditing { get; }
    public IRemotePluginsBackend? Plugins { get; }
    public IRemoteNetworkBackend? Network { get; }
    public IRemoteEditorBackend? Editor { get; }

    public Task<HostInfo> InfoAsync(CancellationToken cancellationToken) => Task.FromResult(_info);

    /** Stop notifications first; release decisions before agents; persist the registry after daemon owners detach. */
    public async ValueTask DisposeAsync()
    {
        await Notifications.DisposeAsync().ConfigureAwait(false);
        _decisions.Dispose();
        Pending.Dispose();
        _tracker.Dispose();
        _runtime.DisposeHandles();
        await _agents.DisposeAsync().ConfigureAwait(false);
        await _ptys.DisposeAsync().ConfigureAwait(false);
        await Sessions.DisposeAsync().ConfigureAwait(false);
    }
}
