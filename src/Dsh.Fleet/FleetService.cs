using Dsh.Boot;
using Dsh.Runtime;

namespace Dsh.Fleet;

/** Plugin-lifetime owner of frontend clients and this Context's outbound route, never target Host sessions. */
public sealed class FleetService : IDisposable, IWorkspaceProvider
{
    public const string ServiceName = "fleet";

    public static IFleetIrohSetup? IrohSetup { get; set; }

    private readonly object _sync = new();
    private readonly List<FleetClient> _clients = [];
    private readonly ISshTransportAdapter? _adapter;
    private readonly Task<IAsyncDisposable>? _runtimeLease;
    private readonly bool _ownsProxy;
    private bool _disposed;

    public FleetService(HarnessHome home, Context? context = null, ISshTransportAdapter? adapter = null)
    {
        Home = home;
        Store = new MachineStore(home);
        _ownsProxy = context?.GetProp(OutboundProxy.PropName) is not OutboundProxy;
        var proxy = _ownsProxy ? new OutboundProxy() : OutboundProxy.FromContext(context!);
        _adapter = adapter;
        OutboundRoute = new BorrowedOutboundRoute(home, proxy, adapter);
        if (context is not null)
        {
            _runtimeLease = OutboundRoute.AcquireAsync().AsTask();
            // Initial acquisition only reads configuration; fail startup explicitly for an invalid saved selection.
            _runtimeLease.GetAwaiter().GetResult();
        }
    }

    public HarnessHome Home { get; }
    public MachineStore Store { get; }
    public BorrowedOutboundRoute OutboundRoute { get; }
    public static ISshTransportAdapter? DefaultSshTransportAdapter { get; set; }

    public ISshTransportAdapter? SshTransportAdapter => _adapter ?? DefaultSshTransportAdapter;

    public IWorkspaceServices Create(HarnessHome home, string clientName, Func<string, CancellationToken, Task>? startLocalHost = null)
    {
        var options = new FleetOptions
        {
            ClientName = clientName,
            LocalHome = Home,
            LocalProxy = OutboundRoute.Proxy,
            AcquireOutboundRoute = OutboundRoute.AcquireAsync,
            SshTransportAdapter = SshTransportAdapter,
            StartLocalHost = startLocalHost is null ? null : (machine, token) => startLocalHost(machine.HarnessHome ?? home.Root, token),
        };
        return new FleetWorkspaceServices(Store, options, () => CreateClient(options), OutboundRoute);
    }

    public FleetClient CreateClient(FleetOptions options)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var config = Store.Load();
            var client = new FleetClient([MachineSpec.Local(Home.Root), .. config.Machines], options with
            {
                Deploy = config.Deploy,
                LocalHome = Home,
                LocalProxy = OutboundRoute.Proxy,
                AcquireOutboundRoute = OutboundRoute.AcquireAsync,
                SshTransportAdapter = options.SshTransportAdapter ?? this.SshTransportAdapter,
            });
            client.Disposed += RemoveClient;
            _clients.Add(client);
            return client;
        }
    }

    private void RemoveClient(FleetClient client)
    {
        client.Disposed -= RemoveClient;
        lock (_sync)
            _clients.Remove(client);
    }

    public void Dispose()
    {
        FleetClient[] clients;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            clients = [.. _clients];
            _clients.Clear();
        }
        // Cancel an in-flight selection before waiting for client/runtime leases that share its gate.
        var routeDisposal = OutboundRoute.DisposeAsync().AsTask();
        List<Exception>? errors = null;
        foreach (var client in clients)
        {
            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
        try
        {
            if (_runtimeLease is not null)
                _runtimeLease.GetAwaiter().GetResult().DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            try
            {
                routeDisposal.GetAwaiter().GetResult();
            }
            finally
            {
                if (_ownsProxy)
                    OutboundRoute.Proxy.Dispose();
            }
        }
        if (errors is not null)
            throw new AggregateException(errors);
    }
}
