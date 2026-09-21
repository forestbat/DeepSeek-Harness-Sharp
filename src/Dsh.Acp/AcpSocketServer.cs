using System.Net;
using System.Net.Sockets;
using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Sdk;

namespace Dsh.Acp;

public sealed class AcpSocketServer : IDisposable
{
    private readonly Context _ctx;
    private readonly string? _provider;
    private readonly string? _model;
    private readonly Func<Context, IDisposable>? _installAgentHooks;
    private readonly string? _socketPath;
    private readonly string? _portFile;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Socket> _clients = [];
    private readonly Lock _gate = new();
    private Socket? _listener;
    private TcpListener? _tcpListener;
    private Task? _acceptLoop;

    public AcpSocketServer(Context ctx, HarnessHome home, string? provider = null, string? model = null,
        Func<Context, IDisposable>? installAgentHooks = null)
    {
        _ctx = ctx;
        _provider = provider;
        _model = model;
        _installAgentHooks = installAgentHooks;
        var runDirectory = Path.Combine(home.Root, "run");
        if (OperatingSystem.IsWindows())
            _portFile = Path.Combine(runDirectory, "dsh-acp.port");
        else
            _socketPath = Path.Combine(runDirectory, "dsh-acp.sock");
    }

    public string Endpoint { get; private set; } = "";

    public void Start()
    {
        if (OperatingSystem.IsWindows())
        {
            _tcpListener = new TcpListener(IPAddress.Loopback, 0);
            _tcpListener.Start();
            var port = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
            Directory.CreateDirectory(Path.GetDirectoryName(_portFile!)!);
            File.WriteAllText(_portFile!, port.ToString());
            Endpoint = $"tcp://127.0.0.1:{port}";
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_socketPath!)!);
            if (File.Exists(_socketPath))
                File.Delete(_socketPath);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath!));
            _listener.Listen(16);
            Endpoint = $"unix://{_socketPath}";
        }
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Dispose();
        _tcpListener?.Stop();
        Socket[] clients;
        lock (_gate)
        {
            clients = [.. _clients];
            _clients.Clear();
        }
        foreach (var client in clients)
            client.Dispose();
        try
        {
            _acceptLoop?.Wait();
        }
        catch (AggregateException)
        {
        }
        TryDelete(_socketPath);
        TryDelete(_portFile);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = _tcpListener is not null
                    ? await _tcpListener.AcceptSocketAsync(cancellationToken)
                    : await _listener!.AcceptAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            lock (_gate)
                _clients.Add(client);
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(Socket socket)
    {
        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            var transport = new JsonRpcLineTransport(reader, writer);
            var server = new AcpServer(_ctx, transport, _provider, _model, _installAgentHooks);
            transport.RequestHandler = server.HandleRequestAsync;
            transport.NotificationHandler = (method, parameters) =>
            {
                if (method == AcpMethods.Cancel)
                    server.Cancel(parameters);
            };
            transport.Start();
            await transport.WhenClosedAsync();
            await server.CloseAllAsync();
            await transport.DisposeAsync();
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            lock (_gate)
                _clients.Remove(socket);
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
