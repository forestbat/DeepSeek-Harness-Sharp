using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;
using Dsh.Runtime;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/**
 * The GUI's one connection to every machine: owns the FleetClient and re-raises its background events on the UI thread.
 * Changed is coalesced (one UI callback per burst of state, session or pending updates); notification events are delivered one by one in arrival order.
 * Reload replaces the client after the machine list was saved, so the views always show the stored machines.
 */
public sealed class FleetWorkspace : IAsyncDisposable
{
    private readonly Func<FleetClient> _factory;
    private readonly DispatcherBridge<bool> _changes;
    private bool _disposed;

    /** store is the machine list the client is built from (the machine management page edits it); options are the connection options before the stored deployment preferences apply. */
    public FleetWorkspace(MachineStore store, FleetOptions options, Func<FleetClient> factory)
    {
        (Store, Options, _factory) = (store, options, factory);
        _changes = new DispatcherBridge<bool>(_ => Changed?.Invoke());
        Client = Attach(factory());
    }

    public MachineStore Store { get; }

    public FleetOptions Options { get; }

    public FleetClient Client { get; private set; }

    /** Raised on the UI thread after any machine's state, sessions or pending requests changed, and after a reload. */
    public event Action? Changed;

    public event Action<MachineConnection, RemoteNotificationChange?>? NotificationsChanged;

    /**
     * This computer's harness home first (it is also the home the GUI itself runs on), then every machine of its store.
     * startLocalHost launches `host serve` for a local machine whose endpoint is not reachable; without it local machines are only connected when their host already runs.
     */
    public static FleetWorkspace ForHome(HarnessHome home, Func<string, CancellationToken, Task>? startLocalHost)
    {
        var store = new MachineStore(home);
        var options = new FleetOptions
        {
            ClientName = GuiSettings.RemoteClientName,
            LocalHome = home,
            SshTransportAdapter = new IrohSshTransport(),
            StartLocalHost = startLocalHost is null ? null : (machine, token) => startLocalHost(machine.HarnessHome ?? home.Root, token),
        };
        return new FleetWorkspace(store, options, () =>
        {
            var config = store.Load();
            return new FleetClient([MachineSpec.Local(home.Root), .. config.Machines], options with { Deploy = config.Deploy });
        });
    }

    /** GUI production connections are created and tracked by the configured Fleet plugin. */
    public static FleetWorkspace ForContext(Context context, HarnessHome home, Func<string, CancellationToken, Task>? startLocalHost)
    {
        var service = context.Get<FleetService>(FleetService.ServiceName)!;
        var options = new FleetOptions
        {
            ClientName = GuiSettings.RemoteClientName,
            LocalHome = service.Home,
            LocalProxy = service.OutboundRoute.Proxy,
            AcquireOutboundRoute = service.OutboundRoute.AcquireAsync,
            SshTransportAdapter = service.SshTransportAdapter,
            StartLocalHost = startLocalHost is null ? null : (machine, token) => startLocalHost(machine.HarnessHome ?? home.Root, token),
        };
        return new FleetWorkspace(service.Store, options, () => service.CreateClient(options));
    }

    public MachineConnection? Find(string machine) => Client.Find(machine);

    public void Start() => Client.Start();

    /** Rebuilds the client from the factory (the machine list may have changed) and starts it. */
    public async Task ReloadAsync()
    {
        var previous = Client;
        Detach(previous);
        Client = Attach(_factory());
        Client.Start();
        _changes.Enqueue(true);
        await previous.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach(Client);
        await Client.DisposeAsync();
    }

    private FleetClient Attach(FleetClient client)
    {
        client.Changed += OnFleetChanged;
        foreach (var machine in client.Machines)
        {
            machine.NotificationsChanged += OnNotificationsChanged;
        }
        return client;
    }

    private void Detach(FleetClient client)
    {
        client.Changed -= OnFleetChanged;
        foreach (var machine in client.Machines)
        {
            machine.NotificationsChanged -= OnNotificationsChanged;
        }
    }

    private void OnFleetChanged() => _changes.Enqueue(true);

    private void OnNotificationsChanged(MachineConnection machine, RemoteNotificationChange? change)
        => Dispatcher.UIThread.Post(() => NotificationsChanged?.Invoke(machine, change));
}
