using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Dsh.Gui.Shared.Mobile;

namespace Dsh.Android.Services;

/**
 * 手机工具的监听器: 把每个 TCP 连接交给一问一答的 HTTP 处理, 由 PhoneMcpHttp 负责协议。
 * 只监听局域网 IPv4, 端口与令牌来自 PhoneToolStore; 令牌是 URL 的一部分, 因此不需要额外鉴权头。
 */
public sealed class PhoneMcpServer(int port, string token) : IAsyncDisposable
{
    private readonly PhoneMcpHttp _http = new(new PhoneMcpProtocol(new AndroidPhoneDevice(), new PhoneUiAutomation()), token);
    private CancellationTokenSource? _lifetime;
    private Task? _loop;

    public int Port { get; } = port;

    public string Token { get; } = token;

    public bool Running => _lifetime is { IsCancellationRequested: false };

    public string? Error { get; private set; }

    /** 电脑端 settings.yaml 里要填的地址: 主地址给一份, 其余地址也一并给出, 让对面挑它够得着的那条。 */
    public string Endpoint => $"http://{LocalAddresses()[0]}:{Port}{PhoneMcpHttp.EndpointPath}/{Token}";

    /** 本机所有可用的 IPv4 地址(局域网、热点、VPN、tailscale 都算); 排序只求稳定, 不判断哪种更"该用"。 */
    public IReadOnlyList<string> Endpoints => [.. LocalAddresses()
        .Select(address => $"http://{address}:{Port}{PhoneMcpHttp.EndpointPath}/{Token}")];

    public void Start()
    {
        if (Running)
            return;
        _lifetime = new CancellationTokenSource();
        var lifetime = _lifetime;
        _loop = AcceptAsync(lifetime.Token);
        global::Android.Util.Log.Info("dsh-mobile", $"phone tools listening on {Endpoint}");
    }

    public async ValueTask DisposeAsync()
    {
        var lifetime = _lifetime;
        _lifetime = null;
        if (lifetime is null)
            return;
        await lifetime.CancelAsync();
        try
        {
            if (_loop is { } loop)
                await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, Port);
        try
        {
            listener.Start();
        }
        catch (SocketException error)
        {
            Error = $"端口 {Port} 无法监听: {error.Message}";
            global::Android.Util.Log.Warn("dsh-mobile", Error);
            listener.Stop();
            return;
        }
        Error = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = ServeAsync(client, cancellationToken);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                await _http.ServeOnceAsync(stream, stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    /**
     * 本机所有可用 IPv4 地址。
     * 手机不判断哪个地址"更稳": 换网、开热点、接 VPN 都会改变可达性, 由电脑侧按自己所在网络挑,
     * 这里只管把自己有的地址如实报出来。回环不报 —— 对外部调用方没有意义。
     */
    private static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up
                    && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(address => address, StringComparer.Ordinal)
                .ToList();
            return addresses.Count > 0 ? addresses : ["127.0.0.1"];
        }
        catch (NetworkInformationException)
        {
            return ["127.0.0.1"];
        }
    }
}
