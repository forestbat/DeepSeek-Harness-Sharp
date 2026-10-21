using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Runtime;

namespace Dsh.WebBridge;

public sealed record WebBridgeOptions(string? HostRoot, int Port, IReadOnlyList<MachineSpec>? Machines = null, FleetOptions? Fleet = null);

/** Implemented by the independent WebHost; the neutral plugin does not load an HTTP framework. */
public interface IWebBridgeApplication
{
    Task<int> RunAsync(WebBridgeRuntime runtime, PluginEntrypointOptions options, CancellationToken cancellationToken);
}

/** The plugin owns the real Fleet links and all bridge streams. Deactivation cancels the listener callback and joins it before releasing the shared client. */
public sealed class WebBridgeRuntime(OutboundProxy? proxy = null, ISshTransportAdapter? adapter = null) : IDisposable
{
    public const string ServiceName = "webBridge";
    public const string ApplicationServiceName = "webBridgeApplication";
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task<int>? _run;
    private bool _disposed;
    public OutboundProxy Proxy { get; } = proxy ?? new OutboundProxy();

    public Task<int> RunAsync(WebBridgeOptions options, Func<FleetClient, CancellationToken, Task<int>> serve, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is not null)
                throw new InvalidOperationException("the web bridge is already running");
            return _run = RunCoreAsync(options, serve, cancellationToken);
        }
    }

    private async Task<int> RunCoreAsync(WebBridgeOptions options, Func<FleetClient, CancellationToken, Task<int>> serve, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        var connectionOptions = options.Fleet ?? new FleetOptions();
        if (connectionOptions.SshTransportAdapter is null && adapter is not null)
            connectionOptions = connectionOptions with { SshTransportAdapter = adapter };
        await using var fleet = new FleetClient(options.Machines ?? [MachineSpec.Local(options.HostRoot)], connectionOptions);
        fleet.Start();
        try { return await serve(fleet, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
    }

    public void Dispose()
    {
        Task<int>? run;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            run = _run;
        }
        try { _stop.Cancel(); run?.GetAwaiter().GetResult(); }
        finally { _stop.Dispose(); }
    }
}
