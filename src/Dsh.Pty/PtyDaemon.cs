using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Dsh.Pty;

public sealed class PtyDaemon : IAsyncDisposable
{
    private readonly PtyHost _host;
    private readonly string? _socketPath;
    private readonly string? _portFile;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Socket> _clientSockets = [];
    private readonly object _gate = new();
    private Socket? _listener;
    private TcpListener? _tcpListener;
    private Task? _acceptLoop;
    private bool _started;

    public PtyDaemon(PtyHost? host = null, string? socketPath = null, string? portFile = null)
    {
        _host = host ?? new PtyHost();
        _socketPath = socketPath ?? (OperatingSystem.IsWindows() ? null : PtyDaemonPaths.SocketPath());
        _portFile = portFile ?? (OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_started)
            return;
        _started = true;

        if (OperatingSystem.IsWindows())
        {
            var portFile = _portFile
                ?? throw new InvalidOperationException("A port file is required for the Windows daemon");
            Directory.CreateDirectory(Path.GetDirectoryName(portFile)!);
            _tcpListener = new TcpListener(IPAddress.Loopback, 0);
            _tcpListener.Start();
            var port = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
            File.WriteAllText(portFile, port.ToString());
        }
        else
        {
            var socketPath = _socketPath
                ?? throw new InvalidOperationException("A socket path is required for the Unix daemon");
            Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
            if (File.Exists(socketPath))
                File.Delete(socketPath);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            _listener.Listen(16);
        }

        _acceptLoop = AcceptLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_started)
            return;
        _started = false;
        _cts.Cancel();

        _listener?.Dispose();
        _listener = null;
        _tcpListener?.Stop();
        _tcpListener = null;

        Socket[] clients;
        lock (_gate)
        {
            clients = _clientSockets.ToArray();
            _clientSockets.Clear();
        }

        foreach (var client in clients)
            client.Dispose();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }

            _acceptLoop = null;
        }

        try
        {
            if (!OperatingSystem.IsWindows() && _socketPath is not null && File.Exists(_socketPath))
                File.Delete(_socketPath);
        }
        catch (IOException)
        {
        }

        try
        {
            if (OperatingSystem.IsWindows() && _portFile is not null && File.Exists(_portFile))
                File.Delete(_portFile);
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _host.Dispose();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            if (OperatingSystem.IsWindows())
            {
                if (_tcpListener is null)
                    return;
                client = await _tcpListener.AcceptSocketAsync(cancellationToken);
            }
            else
            {
                if (_listener is null)
                    return;
                client = await _listener.AcceptAsync(cancellationToken);
            }

            lock (_gate)
                _clientSockets.Add(client);
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLineAsync(stream, cancellationToken);
                if (line is null)
                    return;

                var request = JsonSerializer.Deserialize<PtyDaemonRequest>(line, PtyDaemonJson.Options);
                if (request is null)
                {
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "invalid request" }, cancellationToken);
                    continue;
                }

                var handled = await HandleRequestAsync(request, stream, cancellationToken);
                if (handled)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            lock (_gate)
                _clientSockets.Remove(socket);
        }
    }

    private async Task<bool> HandleRequestAsync(PtyDaemonRequest request, NetworkStream stream, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "list":
                await WriteResponseAsync(stream, new PtyDaemonResponse
                {
                    Ok = true,
                    Sessions = _host.List().Select(ToDto).ToList(),
                }, cancellationToken);
                return false;

            case "start":
            {
                if (request.Params is not { FileName.Length: > 0 } parameters)
                {
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "start requires params.fileName" }, cancellationToken);
                    return false;
                }

                try
                {
                    var startInfo = new PtyStartInfo
                    {
                        FileName = parameters.FileName,
                        Arguments = parameters.Arguments,
                        WorkingDirectory = parameters.WorkingDirectory,
                        Environment = parameters.Environment,
                        Rows = parameters.Rows,
                        Columns = parameters.Columns,
                    };
                    var session = await _host.StartAsync(startInfo, parameters.Id, cancellationToken);
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true, Session = ToDto(session.ToInfo()) }, cancellationToken);
                }
                catch (Exception error)
                {
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = error.Message }, cancellationToken);
                }

                return false;
            }

            case "attach":
            {
                var id = request.Id;
                if (id is null)
                {
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "attach requires id" }, cancellationToken);
                    return false;
                }

                var session = _host.Get(id);
                if (session is null)
                {
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = $"PTY session not found: {id}" }, cancellationToken);
                    return false;
                }

                if (session.Status != PtySessionStatus.Running)
                {
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = $"PTY session {id} is not running" }, cancellationToken);
                    return false;
                }

                session.Attach();
                try
                {
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true, Session = ToDto(session.ToInfo()) }, cancellationToken);
                    await TunnelAsync(session, stream, cancellationToken);
                }
                finally
                {
                    session.Detach();
                }

                return true;
            }

            default:
                await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = $"unknown method: {request.Method}" }, cancellationToken);
                return false;
        }
    }

    private static async Task TunnelAsync(PtySession session, NetworkStream stream, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpToSession = PumpSocketToSessionAsync(session, stream, linked.Token);
        var pumpToSocket = PumpSessionToSocketAsync(session, stream, linked.Token);
        await Task.WhenAny(pumpToSession, pumpToSocket);
        linked.Cancel();

        try
        {
            await pumpToSession;
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await pumpToSocket;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task PumpSocketToSessionAsync(PtySession session, NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return;
            await session.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task PumpSessionToSocketAsync(PtySession session, NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await session.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return;
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private static async Task WriteResponseAsync(Stream stream, PtyDaemonResponse response, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, PtyDaemonJson.Options) + "\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single, cancellationToken);
            if (read == 0)
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (single[0] == (byte)'\n')
                return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(single[0]);
        }
    }

    private static PtyDaemonSessionDto ToDto(PtySessionInfo info)
        => new()
        {
            Id = info.Id.ToString(),
            Command = info.Command,
            StartedAt = info.StartedAt,
            Pid = info.Pid,
            Status = info.Status.ToString(),
            ExitCode = info.ExitCode,
            IsAttached = info.IsAttached,
        };
}