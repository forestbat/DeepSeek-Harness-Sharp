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

    private SshOutboundTunnel CreateTunnel(string machine)
    {
        var spec = new MachineStore(home).Load().Find(machine)
            ?? throw new InvalidOperationException("The selected outbound machine does not exist.");
        return new SshOutboundTunnel(spec, adapter);
    }

    private async Task ValidateAsync(SshOutboundTunnel draft, CancellationToken cancellationToken)
    {
        var configured = HarnessSettings.Load(home).Network?.OutboundProxy?.ProbeUrl;
        var probe = string.IsNullOrWhiteSpace(configured) ? OutboundProxySettings.DefaultProbeUrl : configured;
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var destination)
            || destination.Scheme is not ("http" or "https") || destination.UserInfo.Length != 0 || destination.Fragment.Length != 0)
            throw new InvalidOperationException("The outbound probe must be an absolute HTTP(S) URL without credentials or a fragment.");
        using var candidate = new OutboundProxy();
        candidate.ConfigureBorrowed(draft.ProxyUri.OriginalString, draft.ConnectAsync);
        if (candidate.ProxyFor(destination) is null)
            throw new InvalidOperationException("The outbound probe bypasses the borrowed route; use a public HTTP(S) destination.");
        using var client = new HttpClient(candidate.CreateHandler(allowAutoRedirect: false));
        using var response = await client.GetAsync(destination, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await body.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
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
