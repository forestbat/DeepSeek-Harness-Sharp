using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

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

    private static readonly TimeSpan ControlReadTimeout = TimeSpan.FromSeconds(30);

    /** 每个 pty 的跨进程归属与输入通道: agent 会话 id、窗格快照、控制消息队列。 */
    private readonly ConcurrentDictionary<string, PtyRemoteState> _remote = new(StringComparer.Ordinal);

    private sealed class PtyRemoteState
    {
        public volatile string? AgentSessionId;

        public volatile List<PtyPaneSnapshotDto>? Panes;

        /** 最近一次 publish-panes 的 UTC ticks; 0 表示未发布(用 Interlocked 读写)。 */
        public long PublishedAtTicks;

        public readonly Channel<PtyControlMessageDto> Controls = Channel.CreateUnbounded<PtyControlMessageDto>();

        public long Seq;
    }

    private PtyRemoteState RemoteFor(string id) => _remote.GetOrAdd(id, _ => new PtyRemoteState());

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
                {
                    var sessions = _host.List();
                    PruneRemote(sessions);
                    await WriteResponseAsync(stream, new PtyDaemonResponse
                    {
                        Ok = true,
                        Sessions = sessions.Select(ToDto).ToList(),
                    }, cancellationToken);
                    return false;
                }

            case "start":
                {
                    if (request.Params is not { FileName.Length: > 0 } parameters)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "start requires params.fileName" }, cancellationToken);
                        return false;
                    }

                    try
                    {
                        var id = parameters.Id ?? $"pty-{Guid.NewGuid():N}";
                        var environment = new Dictionary<string, string?>(
                            parameters.Environment ?? new Dictionary<string, string?>())
                        {
                            [PtySessionProtocol.SessionVariable] = id,
                        };
                        var startInfo = new PtyStartInfo
                        {
                            FileName = parameters.FileName,
                            Arguments = parameters.Arguments,
                            WorkingDirectory = parameters.WorkingDirectory,
                            Environment = environment,
                            Rows = parameters.Rows,
                            Columns = parameters.Columns,
                            WantsMouse = parameters.WantsMouse,
                        };
                        var session = await _host.StartAsync(startInfo, id, cancellationToken);
                        WriteSessionSize(session);
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true, Session = ToDto(session.ToInfo()) }, cancellationToken);
                    }
                    catch (Exception error)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = error.Message }, cancellationToken);
                    }

                    return false;
                }

            case "resize":
                {
                    var id = request.Id;
                    var session = id is null ? null : _host.Get(id);
                    if (session is null)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = $"PTY session not found: {id}" }, cancellationToken);
                        return false;
                    }

                    if (request.Params is { } size)
                        session.Resize(size.Rows, size.Columns);
                    WriteSessionSize(session);
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true, Session = ToDto(session.ToInfo()) }, cancellationToken);
                    return false;
                }

            case "mouse":
                {
                    // 终端代理把宿主终端的鼠标事件送进来: 注入常驻会话的控制台输入缓冲, 不能写 pty
                    // (ConPTY 不翻译鼠标转义序列, 写 pty 只会在记录读取型 TUI 里变成乱码文本)。
                    var id = request.Id;
                    var session = id is null ? null : _host.Get(id);
                    if (session is null)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = $"PTY session not found: {id}" }, cancellationToken);
                        return false;
                    }

                    session.InjectMouse(new PtyMouseEvent(request.MouseX, request.MouseY, request.MouseButtonState, request.MouseEventFlags));
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true }, cancellationToken);
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

                    // 新 proxy 接管前先丢掉队列里的历史输出(含早先的 detach 标记): 画面由快照重建, 历史字节只会误导。
                    session.DrainPendingOutput();
                    session.Attach();
                    try
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true, Session = ToDto(session.ToInfo()) }, cancellationToken);                        // 状态重建: 用会话的 VT 屏全量重绘(清屏+定位+SGR+光标), 空闲/全屏程序接管后都是正确画面。
                        await stream.WriteAsync(session.Snapshot(), cancellationToken);
                        await TunnelAsync(session, stream, cancellationToken);
                    }
                    finally
                    {
                        session.Detach();
                    }

                    return true;
                }

            case "identify":
                {
                    if (request.Id is not { Length: > 0 } id)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "identify requires id" }, cancellationToken);
                        return false;
                    }

                    RemoteFor(id).AgentSessionId = request.AgentSessionId;
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true }, cancellationToken);
                    return false;
                }

            case "publish-panes":
                {
                    if (request.Id is not { Length: > 0 } id)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "publish-panes requires id" }, cancellationToken);
                        return false;
                    }

                    var remote = RemoteFor(id);
                    if (request.AgentSessionId is { Length: > 0 } sessionId)
                        remote.AgentSessionId = sessionId;
                    remote.Panes = request.Panes;
                    Interlocked.Exchange(ref remote.PublishedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true }, cancellationToken);
                    return false;
                }

            case "control-send":
                {
                    if (request.Id is not { Length: > 0 } id)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "control-send requires id" }, cancellationToken);
                        return false;
                    }

                    var remote = RemoteFor(id);
                    var seq = Interlocked.Increment(ref remote.Seq);
                    remote.Controls.Writer.TryWrite(new PtyControlMessageDto
                    {
                        Seq = seq,
                        Kind = request.Kind ?? "text",
                        PaneId = request.PaneId,
                        Payload = request.Payload ?? "",
                    });
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true, Seq = seq }, cancellationToken);
                    return false;
                }

            case "control-read":
                {
                    if (request.Id is not { Length: > 0 } id)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "control-read requires id" }, cancellationToken);
                        return false;
                    }

                    var controls = await ReadControlsAsync(RemoteFor(id), request.SinceSeq, cancellationToken);
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true, Controls = controls }, cancellationToken);
                    return false;
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

    private PtyDaemonSessionDto ToDto(PtySessionInfo info)
    {
        var dto = new PtyDaemonSessionDto
        {
            Id = info.Id.ToString(),
            Command = info.Command,
            StartedAt = info.StartedAt,
            Pid = info.Pid,
            Status = info.Status.ToString(),
            ExitCode = info.ExitCode,
            IsAttached = info.IsAttached,
            Columns = info.Columns,
            Rows = info.Rows,
            WantsMouse = info.WantsMouse,
        };
        if (_remote.TryGetValue(info.Id.Value, out var remote))
        {
            dto.AgentSessionId = remote.AgentSessionId;
            dto.Panes = remote.Panes;
            var publishedTicks = Volatile.Read(ref remote.PublishedAtTicks);
            dto.PublishedAt = publishedTicks == 0 ? null : new DateTimeOffset(publishedTicks, TimeSpan.Zero);
        }
        return dto;
    }

    /** 清掉已不存在的 pty 的归属与输入通道, 避免 list 长期累积。 */
    private void PruneRemote(IReadOnlyList<PtySessionInfo> sessions)
    {
        if (_remote.IsEmpty)
            return;
        var alive = sessions.Select(info => info.Id.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _remote.Keys)
        {
            if (!alive.Contains(id))
                _remote.TryRemove(id, out _);
        }
    }

    /** 取 sinceSeq 之后的新控制消息; 无消息时最长等待 ControlReadTimeout(长轮询)。 */
    private static async Task<List<PtyControlMessageDto>> ReadControlsAsync(PtyRemoteState remote, long sinceSeq, CancellationToken cancellationToken)
    {
        var messages = new List<PtyControlMessageDto>();
        Drain(remote, sinceSeq, messages);
        if (messages.Count > 0)
            return messages;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ControlReadTimeout);
        try
        {
            if (await remote.Controls.Reader.WaitToReadAsync(timeout.Token))
                Drain(remote, sinceSeq, messages);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        return messages;
    }

    private static void Drain(PtyRemoteState remote, long sinceSeq, List<PtyControlMessageDto> messages)
    {
        while (remote.Controls.Reader.TryRead(out var message))
        {
            if (message.Seq > sinceSeq)
                messages.Add(message);
        }
    }

    /** 会话尺寸落盘: ConPTY 子进程读不到 resize 后的窗口尺寸, 由尺寸的权威方(daemon)写文件给常驻 TUI 自己读。 */
    private static void WriteSessionSize(PtySession session)
    {
        try
        {
            var info = session.ToInfo();
            Directory.CreateDirectory(PtyDaemonPaths.RunDirectory());
            File.WriteAllText(
                PtyDaemonPaths.SessionSizeFile(session.Id.ToString()),
                $"{info.Columns} {info.Rows}");
        }
        catch (IOException)
        {
        }
    }
}