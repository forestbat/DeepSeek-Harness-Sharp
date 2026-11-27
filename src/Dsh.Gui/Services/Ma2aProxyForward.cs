using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dsh.Ma2a;
using Dsh.Ma2a.Services;

namespace Dsh.Gui.Services;

/** A local-only listener; each accepted socket gets its own authorized native Proxy stream. */
public sealed class Ma2aProxyForward : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime;
    private readonly Ma2aClient _client;
    private readonly string _endpoint;
    private readonly JsonNode _body;
    private readonly Action<string> _report;
    private readonly HashSet<Task> _connections = [];
    private readonly Task _accepting;
    private const int MaximumConnections = 64;

    public Ma2aProxyForward(Ma2aClient client, string endpoint, JsonNode body, ushort port,
        Action<string> report, CancellationToken cancellationToken)
    {
        _client = client;
        _endpoint = endpoint;
        _body = body;
        _report = report;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    public int LocalPort { get; }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var socket = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                _connections.RemoveWhere(task => task.IsCompleted);
                if (_connections.Count >= MaximumConnections) { socket.Dispose(); continue; }
                _connections.Add(ForwardAsync(socket));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error) { _report(error.Message); }
        finally { _listener.Stop(); }
    }

    private async Task ForwardAsync(TcpClient socket)
    {
        using (socket)
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            try
            {
                var requestId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                await using var session = await Ma2aServiceSession.OpenAsync(_client, _endpoint, "proxy", requestId, _body, connection.Token);
                var admission = await session.ReadAsync(connection.Token);
                if (admission.Kind == Ma2aFrameKind.Outcome) return;
                var sending = SendAsync(socket.GetStream(), session, connection.Token);
                var receiving = ReceiveAsync(socket, session, connection.Token);
                await Task.WhenAny(sending, receiving);
                if (receiving.IsCompleted || sending.IsFaulted) await connection.CancelAsync();
                await Task.WhenAll(sending, receiving);
            }
            catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
            catch (Exception error) { _report($"Proxy: {error.Message}"); }
        }
    }

    private static async Task SendAsync(NetworkStream socket, Ma2aServiceSession session, CancellationToken cancellationToken)
    {
        var buffer = new byte[Ma2aServiceSession.MaximumDataLength];
        int read;
        while ((read = await socket.ReadAsync(buffer, cancellationToken)) > 0)
            await session.SendDataAsync(0, buffer.AsMemory(0, read), cancellationToken);
        await session.EndAsync(cancellationToken: cancellationToken);
    }

    private static async Task ReceiveAsync(TcpClient socket, Ma2aServiceSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken);
            if (frame.Kind is Ma2aFrameKind.Data or Ma2aFrameKind.End && frame.Channel != 0)
                throw new InvalidDataException("Proxy received a non-proxy data channel.");
            if (frame.Kind == Ma2aFrameKind.Data) await socket.GetStream().WriteAsync(frame.Data, cancellationToken);
            else if (frame.Kind == Ma2aFrameKind.End) socket.Client.Shutdown(SocketShutdown.Send);
            else if (frame.Kind == Ma2aFrameKind.Outcome) return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener.Stop();
        await _accepting;
        await Task.WhenAll(_connections);
        _lifetime.Dispose();
    }
}
