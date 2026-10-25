using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Fleet;

/// <summary>Owns one Context's selection and asynchronous consumer leases, not any target Host or PTY.</summary>
public sealed class BorrowedOutboundRoute(HarnessHome home, OutboundProxy proxy, ISshTransportAdapter? adapter = null) : IOutboundRouteController, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeSync = new();
    private Task? _disposal;
    private SshOutboundTunnel? _tunnel;
    private string? _selected;
    private int _consumers;
    private bool _disposed;
    private bool _invalidSavedConfiguration = true;

    public OutboundProxy Proxy { get; } = proxy;

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_consumers == 0)
                await ApplySavedAsync(cancellationToken, activate: true, initialAcquisition: true).ConfigureAwait(false);
            _consumers++;
            return new Lease(this);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SelectAsync(string? machine, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SshOutboundTunnel? draft = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            if (machine is not null)
            {
                draft = CreateTunnel(machine);
                await ValidateAsync(draft, operation.Token).ConfigureAwait(false);
            }
            operation.Token.ThrowIfCancellationRequested();
            HarnessSettings.SaveBorrowedOutboundMachine(home, machine);
            // Once saved, finish the cutover even if this caller cancels; never restore a different persisted selection.
            await RetireAsync().ConfigureAwait(false);
            _selected = machine;
            _invalidSavedConfiguration = false;
            if (_consumers > 0)
            {
                Publish(draft);
                _tunnel = draft;
                draft = null;
            }
            else if (machine is null)
                Proxy.ConfigureBorrowed(null, null);
        }
        finally
        {
            try
            {
                if (draft is not null)
                    await draft.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ApplySavedAsync(cancellationToken, activate: _consumers > 0).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /** Checks a candidate without saving it or replacing any consumer's current route. */
    public async Task<ProxyProbeResult> ProbeAsync(string machine, Guid operationId, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        operation.CancelAfter(FleetProxyProbe.LocalTimeout);
        string? proxyUrl = null;
        try
        {
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
            SshOutboundTunnel? candidate = null;
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var saved = LoadMachine(machine);
                proxyUrl = saved.OutboundTunnel?.ProxyUrl;
                var proxyUri = MachineStore.ValidateOutboundTunnel(saved)
                    ?? throw new InvalidOperationException("The machine has no outbound tunnel configured.");
                proxyUrl = proxyUri.OriginalString;
                var draft = await CreateProbeCandidateAsync(saved, proxyUri, operation.Token).ConfigureAwait(false);
                if (draft.OwnsTunnel)
                    candidate = draft.Tunnel;
                var status = await ValidateAsync(draft.Tunnel, operation.Token, requireSuccess: false, proxyUrl: proxyUrl).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                var authenticationFailed = status == (int)System.Net.HttpStatusCode.ProxyAuthenticationRequired;
                return new ProxyProbeResult(ProxyProbeLayer.LocalOutbound,
                    authenticationFailed ? ProxyProbeOutcome.Failed : ProxyProbeOutcome.Passed,
                    authenticationFailed ? ProxyProbeStage.ProxyAuth : ProxyProbeStage.HttpTarget,
                    $"借用出口返回 HTTP {status}；{(authenticationFailed ? "代理认证失败" : "出口可达，不代表业务成功")}。{(draft.UsesTemporaryPort ? "独立检查使用临时本机端口；已保存端口和当前消费者保持不变。" : "")}",
                    status, machine, home.Root, operationId);
            }
            finally
            {
                try
                {
                    if (candidate is not null)
                        await candidate.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new ProxyProbeResult(ProxyProbeLayer.LocalOutbound,
                cancellationToken.IsCancellationRequested ? ProxyProbeOutcome.Cancelled : ProxyProbeOutcome.Failed,
                ProxyProbeStage.Tunnel, cancellationToken.IsCancellationRequested
                    ? "借用出口验证已取消。"
                    : "借用出口验证超时或所属运行期已停止。",
                null, machine, home.Root, operationId);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            var authenticationFailed = error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.ProxyAuthenticationRequired };
            return new ProxyProbeResult(ProxyProbeLayer.LocalOutbound, ProxyProbeOutcome.Failed,
                authenticationFailed ? ProxyProbeStage.ProxyAuth : ProxyProbeStage.Tunnel,
                OutboundProxy.Redact(error.Message, proxyUrl),
                error is HttpRequestException { StatusCode: { } status } ? (int)status : null,
                machine, home.Root, operationId);
        }
    }

    private async Task ApplySavedAsync(CancellationToken cancellationToken, bool activate, bool initialAcquisition = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SshOutboundTunnel? next = null;
        var hasBorrowedSelection = true;
        try
        {
            var settings = HarnessSettings.Load(home).Network;
            var selected = settings?.BorrowedOutboundMachine;
            hasBorrowedSelection = selected is not null;
            var baseline = settings?.OutboundProxy;
            using var validation = new OutboundProxy();
            validation.Configure(baseline?.Url, baseline?.Bypass);
            if (selected is not null)
                next = CreateTunnel(selected);
            cancellationToken.ThrowIfCancellationRequested();
            if (!initialAcquisition || selected is not null)
                await RetireAsync().ConfigureAwait(false);
            Proxy.Configure(baseline?.Url, baseline?.Bypass);
            _selected = selected;
            _invalidSavedConfiguration = false;
            if (activate)
            {
                Publish(next);
                _tunnel = next;
                next = null;
            }
            else if (selected is null)
                Proxy.ConfigureBorrowed(null, null);
        }
        catch (ArgumentException) when (!hasBorrowedSelection && initialAcquisition)
        {
            // Boot already reports invalid baselines without disabling direct-only startup.
            _selected = null;
            _invalidSavedConfiguration = false;
            Proxy.ConfigureBorrowed(null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (hasBorrowedSelection)
            {
                // An invalid saved selection must fail closed, including when a caller catches the reload error.
                _invalidSavedConfiguration = true;
                await RetireAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (next is not null)
                await next.DisposeAsync().ConfigureAwait(false);
        }
    }

    private MachineSpec LoadMachine(string machine)
        => new MachineStore(home).Load().Find(machine)
            ?? throw new InvalidOperationException("The selected outbound machine does not exist.");

    private SshOutboundTunnel CreateTunnel(string machine) => new(LoadMachine(machine), adapter);

    private async ValueTask<(SshOutboundTunnel Tunnel, bool OwnsTunnel, bool UsesTemporaryPort)> CreateProbeCandidateAsync(
        MachineSpec machine, Uri proxyUri, CancellationToken cancellationToken)
    {
        var current = _selected == machine.Name ? _tunnel : null;
        if (current?.HasSameTransport(machine, proxyUri) == true)
            return (current, false, false);
        var temporaryPort = false;
        if (current is not null && machine.OutboundTunnel is { Mode: "localForward", LocalPort: { } port } forwarding
            && await current.OwnsForwardPortAsync(port, cancellationToken).ConfigureAwait(false))
        {
            machine = machine with { OutboundTunnel = forwarding with { LocalPort = null } };
            temporaryPort = true;
        }
        return (new SshOutboundTunnel(machine, adapter), true, temporaryPort);
    }

    private async Task<int> ValidateAsync(SshOutboundTunnel draft, CancellationToken cancellationToken, bool requireSuccess = true,
        string? proxyUrl = null)
    {
        var configured = HarnessSettings.Load(home).Network?.OutboundProxy?.ProbeUrl;
        var probe = string.IsNullOrWhiteSpace(configured) ? OutboundProxySettings.DefaultProbeUrl : configured;
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var destination)
            || destination.Scheme is not ("http" or "https") || destination.UserInfo.Length != 0 || destination.Fragment.Length != 0)
            throw new InvalidOperationException("The outbound probe must be an absolute HTTP(S) URL without credentials or a fragment.");
        await using var connections = new ProbeConnections(draft);
        using var candidate = new OutboundProxy();
        candidate.ConfigureBorrowed(proxyUrl ?? draft.ProxyUri.OriginalString, connections.ConnectAsync);
        if (candidate.ProxyFor(destination) is null)
            throw new InvalidOperationException("The outbound probe bypasses the borrowed route; use a public HTTP(S) destination.");
        using var client = new HttpClient(candidate.CreateHandler(allowAutoRedirect: false));
        using var response = await client.GetAsync(destination, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (requireSuccess)
        {
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await body.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        }
        return (int)response.StatusCode;
    }

    /** Awaits only this HTTP probe's connections, including attempts completed after request cancellation. */
    private sealed class ProbeConnections(SshOutboundTunnel tunnel) : IAsyncDisposable
    {
        private readonly List<Task<Stream>> _connections = [];
        private bool _disposed;

        public ValueTask<Stream> ConnectAsync(System.Net.DnsEndPoint endpoint, CancellationToken cancellationToken)
        {
            lock (_connections)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var connection = tunnel.ConnectAsync(endpoint, cancellationToken).AsTask();
                _connections.Add(connection);
                return new ValueTask<Stream>(connection);
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_connections)
                _disposed = true;
            List<Exception>? errors = null;
            foreach (var connection in _connections)
            {
                try
                {
                    var stream = await connection.ConfigureAwait(false);
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    (errors ??= []).Add(error);
                }
            }
            if (errors is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(errors[0]);
            if (errors is not null)
                throw new AggregateException("Borrowed probe connection cleanup failed.", errors);
        }
    }

    private void Publish(SshOutboundTunnel? tunnel)
        => Proxy.ConfigureBorrowed(tunnel?.ProxyUri.OriginalString, tunnel is null ? null : tunnel.ConnectAsync);

    private async Task RetireAsync()
    {
        // Retire pools and credentials before awaiting owned SSH shutdown. This is never a direct-route fallback.
        Proxy.SuspendBorrowed();
        var previous = _tunnel;
        _tunnel = null;
        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask ReleaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || --_consumers != 0)
                return;
            await RetireAsync().ConfigureAwait(false);
            if (_selected is null && !_invalidSavedConfiguration)
                Proxy.ConfigureBorrowed(null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
            return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _consumers = 0;
            await RetireAsync().ConfigureAwait(false);
            if (_selected is null && !_invalidSavedConfiguration)
                Proxy.ConfigureBorrowed(null, null);
        }
        finally
        {
            _gate.Release();
            _lifetime.Dispose();
        }
    }

    private sealed class Lease(BorrowedOutboundRoute owner) : IAsyncDisposable
    {
        private readonly object _sync = new();
        private BorrowedOutboundRoute? _owner = owner;
        private Task? _disposal;

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposal is null)
                {
                    var active = _owner!;
                    _owner = null;
                    _disposal = active.ReleaseAsync().AsTask();
                }
                return new ValueTask(_disposal);
            }
        }
    }
}
