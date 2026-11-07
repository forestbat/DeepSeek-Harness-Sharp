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

    /** 电脑端 settings.yaml 里要填的地址。 */
    public string Endpoint => $"http://{LocalAddress()}:{Port}{PhoneMcpHttp.EndpointPath}/{Token}";

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

    private static string LocalAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up
                    && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                // 同一台手机可能同时有局域网与 tailscale(100.64/10) 地址: 优先给局域网, 电脑通常就在同一个网段。
                .OrderBy(address => IsCarrierGradeNat(address) ? 1 : 0)
                .FirstOrDefault()?.ToString()
                ?? "127.0.0.1";
        }
        catch (NetworkInformationException)
        {
            return "127.0.0.1";
        }
    }

    private static bool IsCarrierGradeNat(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }
}
