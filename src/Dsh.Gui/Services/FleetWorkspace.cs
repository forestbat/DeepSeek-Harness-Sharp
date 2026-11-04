using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Workspace;
using Dsh.Runtime;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/**
 * The GUI's one connection to every machine: owns the IWorkspaceClient and re-raises its background events on the UI thread.
 * Changed is coalesced (one UI callback per burst of state, session or pending updates); notification events are delivered one by one in arrival order.
 * Reload replaces the client after the machine list was saved, so the views always show the stored machines.
 */
public sealed class FleetWorkspace : IAsyncDisposable
{
    private readonly Func<IWorkspaceClient> _factory;
    private readonly DispatcherBridge<bool> _changes;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private Task? _disposal;
    private bool _disposed;

    /** Each workspace owns its connections; the configured services own backend construction and administration. */
    public FleetWorkspace(IWorkspaceServices services)
    {
        Services = services;
        _factory = services.CreateClient;
        _changes = new DispatcherBridge<bool>(_ => Changed?.Invoke());
        Client = Attach(_factory());
    }

    public IWorkspaceServices Services { get; }

    public IMachineCatalog Store => Services.Store;

    public IWorkspaceClient Client { get; private set; }

    /** Raised on the UI thread after any machine's state, sessions or pending requests changed, and after a reload. */
    public event Action? Changed;

    public event Action<IWorkspaceMachine, RemoteNotificationChange?>? NotificationsChanged;

    /** Resolve the configured backend capability without choosing its transport or implementation. */
    public static FleetWorkspace ForContext(Context context, HarnessHome home, Func<string, CancellationToken, Task>? startLocalHost)
        => new(context.Get<IWorkspaceProvider>(IWorkspaceProvider.ServiceName)!
            .Create(home, GuiSettings.RemoteClientName, startLocalHost));

    public IWorkspaceMachine? Find(string machine) => Client.Find(machine);

    public void Start() => Client.Start();

    /** Rebuilds the client from the factory (the machine list may have changed) and starts it. */
    public async Task ReloadAsync()
    {
        await _reloadGate.WaitAsync(_lifetime.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var replacement = _factory();
            var previous = Client;
            Detach(previous);
            Client = Attach(replacement);
            Client.Start();
            _changes.Enqueue(true);
            await previous.DisposeAsync();
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /** A same-home Host owns its independent runtime; reload it without replacing its connection or consumers. */
    public async Task ReloadLocalOutboundAsync(string home, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _reloadGate.WaitAsync(operation.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var connection in Client.Machines)
            {
                if (!connection.Machine.IsLocal || connection.State != MachineConnectionState.Connected
                    || connection.Info?.HomeRoot is not { } actual)
                    continue;
                if (string.Equals(canonical, Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)), comparison))
                    await connection.Network.ReloadLocalAsync(canonical, operation.Token);
            }
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        await _reloadGate.WaitAsync();
        try
        {
            Detach(Client);
            await Client.DisposeAsync();
        }
        finally
        {
            _reloadGate.Release();
            _reloadGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private IWorkspaceClient Attach(IWorkspaceClient client)
    {
        client.Changed += OnFleetChanged;
        foreach (var machine in client.Machines)
        {
            machine.NotificationsChanged += OnNotificationsChanged;
        }
        return client;
    }

    private void Detach(IWorkspaceClient client)
    {
        client.Changed -= OnFleetChanged;
        foreach (var machine in client.Machines)
        {
            machine.NotificationsChanged -= OnNotificationsChanged;
        }
    }

    private void OnFleetChanged() => _changes.Enqueue(true);

    private void OnNotificationsChanged(IWorkspaceMachine machine, RemoteNotificationChange? change)
        => Dispatcher.UIThread.Post(() => NotificationsChanged?.Invoke(machine, change));
}
