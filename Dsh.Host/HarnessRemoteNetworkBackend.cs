using System.Security.Cryptography;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Host;

internal sealed class HarnessRemoteNetworkBackend(HarnessHome home, OutboundProxy proxy, IOutboundRouteController? controller = null) :
   IRemoteNetworkBackend, IRemoteNetworkReloadBackend
{
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly string _ownerId = Guid.NewGuid().ToString("N");

    public async Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(request.ExpectedRevision, ReadState().Revision, StringComparison.Ordinal))
                throw new JsonRpcException(NetworkProtocol.RevisionConflict, "outbound proxy settings changed; reload before saving");
            cancellationToken.ThrowIfCancellationRequested();
            HarnessSettings.SaveOutboundProxy(home, ToLocalSettings(request.Settings), proxy);
            return ReadState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken)
    {
        OutboundProxySettings settings;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings = ToLocalSettings(request.Draft ?? ReadSettings());
        }
        finally
        {
            _gate.Release();
        }
        if (request.ProbeUrl is { } probeUrl)
            settings.ProbeUrl = probeUrl;
        var result = await OutboundProxyProbe.RunAsync(settings, cancellationToken, "远端服务器出口");
        return new RemoteOutboundProxyProbeResult(result.Reachable, result.Message, result.Stage, result.HttpStatus, home.Root);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (controller is not null)
            {
                await controller.ReloadAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            var settings = HarnessSettings.Load(home).Network;
            if (settings?.BorrowedOutboundMachine is not null)
            {
                proxy.SuspendBorrowed();
                throw new InvalidOperationException("The Host cannot apply a borrowed selection without its outbound route controller.");
            }
            proxy.Configure(settings?.OutboundProxy?.Url, settings?.OutboundProxy?.Bypass);
            proxy.ConfigureBorrowed(null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RemoteOutboundProxyState ReadState()
    {
        var settings = ReadSettings();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(settings, NetworkProtocolJsonContext.Default.RemoteOutboundProxySettings);
        var revision = Convert.ToHexStringLower(SHA256.HashData(canonical));
        return new RemoteOutboundProxyState(_ownerId, home.Root, revision, settings);
    }

    private RemoteOutboundProxySettings ReadSettings()
    {
        var settings = HarnessSettings.Load(home).Network?.OutboundProxy;
        return new RemoteOutboundProxySettings(settings?.Url, settings?.Bypass?.ToArray() ?? [], settings?.ProbeUrl);
    }

    private static OutboundProxySettings ToLocalSettings(RemoteOutboundProxySettings settings) => new()
    {
        Url = settings.Url,
        Bypass = settings.Bypass?.ToList() ?? [],
        ProbeUrl = settings.ProbeUrl,
    };
}
