using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.Transport;

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
    private LocalEndpointToken? _token;
    private Task? _acceptLoop;
    private bool _started;

    private static readonly TimeSpan ControlReadTimeout = TimeSpan.FromSeconds(30);

    /** 每个 pty 的跨进程归属与输入通道: agent 会话 id、窗格快照、控制消息队列。 */
    private readonly ConcurrentDictionary<string, PtyRemoteState> _remote = new(StringComparer.Ordinal);
    private readonly string? _runRoot;

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
        // 记录与尺寸文件跟着端点走: 测试用临时端点时, 不能写进用户真实的 ~/.dsh/run(反之亦然)。
        _runRoot = Path.GetDirectoryName(_portFile ?? _socketPath ?? PtyDaemonPaths.SocketPath());
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
            // The token must exist before the port file announces the endpoint: clients send it with their first request.
            _token = LocalEndpointToken.Issue(portFile);
            _tcpListener = new TcpListener(IPAddress.Loopback, 0);
            _tcpListener.Start();
            var port = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
            File.WriteAllText(portFile, port.ToString());
        }
        else
        {
            var socketPath = _socketPath
                ?? throw new InvalidOperationException("A socket path is required for the Unix daemon");
            _token = LocalEndpointToken.Issue(socketPath);
            _listener = OwnerOnlyFiles.ListenUnixSocket(socketPath, 16);
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

        // The field stays set: handlers still draining after Stop must keep rejecting, not dereference null.
        _token?.Dispose();
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
            Socket? client;
            try
            {
                client = await AcceptAsync(cancellationToken);
            }
            catch (Exception error) when (cancellationToken.IsCancellationRequested
                && error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // StopAsync can dispose the listener between the loop check and the next accept (CI: ObjectDisposedException from DisposeAsync on Windows).
                return;
            }

            if (client is null)
                return;
            lock (_gate)
                _clientSockets.Add(client);
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    /** Null once StopAsync has released the listener. */
    private async Task<Socket?> AcceptAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return _tcpListener is { } tcp ? await tcp.AcceptSocketAsync(cancellationToken) : null;
        return _listener is { } unix ? await unix.AcceptAsync(cancellationToken) : null;
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLineAsync(stream, LineProtocolLimits.MaxLineBytes, cancellationToken);
                if (line is null)
                    return;

                var request = ParseRequest(line);
                if (request is null || !_token!.Matches(request.Token))
                {
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = request is null ? "invalid request" : "unauthorized" }, cancellationToken);
                    return;
                }

                var handled = await HandleRequestAsync(request, stream, cancellationToken);
                if (handled)
                    return;
            }
        }
        catch (InvalidDataException)
        {
            // Request line over LineProtocolLimits.MaxLineBytes: drop the connection without buffering the rest.
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

    private static PtyDaemonRequest? ParseRequest(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, PtyDaemonJsonContext.Default.PtyDaemonRequest);
        }
        catch (JsonException)
        {
            return null;
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

            case "probe":
                {
                    // "这个 id 是不是(或曾经是)一个 pty?": CLI 据此区分"接管 daemon 的 pty"与"恢复 harness 会话"。
                    // 已退出但有记录的 pty 也算认识 —— attach 会按记录把它复活。
                    if (request.Id is not { Length: > 0 } id)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "probe requires id" }, cancellationToken);
                        return false;
                    }

                    var live = _host.Get(id);
                    var recorded = PtyRecords.TryLoad(id, _runRoot);
                    var known = live is not null || recorded is not null;
                    await WriteResponseAsync(stream, new PtyDaemonResponse
                    {
                        Ok = known,
                        Error = known ? null : $"PTY session not found: {id}",
                        Session = live is not null ? ToDto(live.ToInfo()) : recorded is null ? null : ToDto(recorded),
                    }, cancellationToken);
                    return false;
                }

            case "kill":
                {
                    if (request.Id is not { Length: > 0 } target)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "kill requires id" }, cancellationToken);
                        return false;
                    }

                    if (!await _host.StopAsync(target))
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = $"PTY session not found: {target}" }, cancellationToken);
                        return false;
                    }

                    PtyRecords.Delete(target, _runRoot);
                    DeleteSizeFile(target);
                    PruneRemote(_host.List());
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true, Killed = [target] }, cancellationToken);
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
                        if (parameters.Home is { Length: > 0 } childHome)
                            environment[PtySessionProtocol.ChildHomeVariable] = childHome;
                        var startInfo = new PtyStartInfo
                        {
                            FileName = parameters.FileName,
                            Arguments = parameters.Arguments,
                            WorkingDirectory = parameters.WorkingDirectory,
                            Environment = environment,
                            Home = parameters.Home,
                            Rows = parameters.Rows,
                            Columns = parameters.Columns,
                            WantsMouse = parameters.WantsMouse,
                        };
                        // 同一个 id 复活时, 旧进程的归属与输入通道都属于上一代, 不能带过来。
                        if (_host.Get(id) is { } previous && previous.Status == PtySessionStatus.Exited)
                        {
                            _remote.TryRemove(id, out _);
                            DeleteSizeFile(id);
                        }
                        var session = await _host.StartAsync(startInfo, id, cancellationToken);
                        PtyRecords.Save(record: new PtyRecord
                        {
                            Id = id,
                            FileName = parameters.FileName,
                            Arguments = parameters.Arguments,
                            WorkingDirectory = parameters.WorkingDirectory,
                            Environment = parameters.Environment,
                            Home = parameters.Home,
                            Rows = parameters.Rows,
                            Columns = parameters.Columns,
                            WantsMouse = parameters.WantsMouse,
                            AgentSessionId = PtyRecords.TryLoad(id, _runRoot)?.AgentSessionId,
                        });
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

            case "input":
                {
                    // 常驻 TUI 的输入旁路: 之后的键盘/粘贴/鼠标字节从这条连接直传, 不再经 pty 的 ConPTY 输入翻译。
                    // 老版本 daemon 不认识这个方法(返回 unauthorized/错误), 调用方据此退回控制台记录路径。
                    var id = request.Id;
                    var session = id is null ? null : _host.Get(id);
                    if (session is null || session.Status != PtySessionStatus.Running)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = $"PTY session not found: {id}" }, cancellationToken);
                        return false;
                    }

                    using var subscription = session.SubscribeInput();
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true }, cancellationToken);
                    await PumpInputToSocketAsync(subscription.Reader, stream, cancellationToken);
                    return true;
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
                    if (session is null || session.Status != PtySessionStatus.Running)
                    {
                        // 已退出(或 daemon 重启后只剩记录)的 pty 按同一 id 复活: 参数来自落盘的记录。
                        if (PtyRecords.TryLoad(id, _runRoot) is not { } record)
                        {
                            await WriteResponseAsync(stream, new PtyDaemonResponse
                            {
                                Ok = false,
                                Error = session is null
                                    ? $"PTY session not found: {id}"
                                    : $"PTY session {id} is not running and has no recorded start parameters"
                                        + AttachRecoveryHint(PtyRecords.TryLoad(id, _runRoot)?.AgentSessionId),
                            }, cancellationToken);
                            return false;
                        }

                        _host.StopAsync(id).GetAwaiter().GetResult();
                        _remote.TryRemove(id, out _);
                        try
                        {
                            session = await _host.StartAsync(StartInfoOf(record), id, cancellationToken);
                        }
                        catch (Exception error)
                        {
                            await WriteResponseAsync(stream, new PtyDaemonResponse
                            {
                                Ok = false,
                                Error = $"PTY session {id} could not be revived: {error.Message}"
                                    + AttachRecoveryHint(record.AgentSessionId),
                            }, cancellationToken);
                            return false;
                        }

                        WriteSessionSize(session);
                    }

                    // 新 proxy 接管前先丢掉队列里的历史输出(含早先的 detach 标记): 画面由快照重建, 历史字节只会误导。
                    session.DrainPendingOutput();
                    session.Attach();
                    try
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true, Session = ToDto(session.ToInfo()) }, cancellationToken);                        // 状态重建: 用会话的 VT 屏全量重绘(清屏+定位+SGR+光标), 空闲/全屏程序接管后都是正确画面。
                        await stream.WriteAsync(session.ConsoleSnapshot(), cancellationToken);
                        await TunnelAsync(session, stream, cancellationToken);
                    }
                    finally
                    {
                        session.Detach();
                    }

                    return true;
                }

            case "subscribe":
                {
                    var id = request.Id;
                    var session = id is null ? null : _host.Get(id);
                    if (session is null)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = $"PTY session not found: {id}" }, cancellationToken);
                        return false;
                    }

                    // 窗格级订阅: 不排空历史、不改 attached 状态, 与独占 attach 并行存在。
                    using var subscription = session.Subscribe();
                    await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = true, Session = ToDto(session.ToInfo()) }, cancellationToken);
                    await TunnelSubscriptionAsync(subscription.Output, session, stream, cancellationToken);
                    return true;
                }

            case "snapshot":
                {
                    var id = request.Id;
                    var session = id is null ? null : _host.Get(id);
                    if (session is null)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = $"PTY session not found: {id}" }, cancellationToken);
                        return false;
                    }

                    await WriteResponseAsync(
                        stream,
                        new PtyDaemonResponse { Ok = true, Snapshot = request.PlainText ? session.SnapshotText() : Encoding.UTF8.GetString(session.Snapshot()) },
                        cancellationToken);
                    return false;
                }

            case "identify":
                {
                    if (request.Id is not { Length: > 0 } id)
                    {
                        await WriteResponseAsync(stream, new PtyDaemonResponse { Ok = false, Error = "identify requires id" }, cancellationToken);
                        return false;
                    }

                    RemoteFor(id).AgentSessionId = request.AgentSessionId;
                    SaveAgentSessionId(id, request.AgentSessionId);
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
                    {
                        remote.AgentSessionId = sessionId;
                        SaveAgentSessionId(id, sessionId);
                    }
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

    /**
     * 窗格级订阅隧道: 输出侧来自会话的订阅通道(首帧是快照), 输入侧原样写入会话。
     * 任一侧结束(客户端断开/会话退出)即收尾; 订阅随 using 释放, 不影响会话本身。
     */
    private static async Task TunnelSubscriptionAsync(
        ChannelReader<byte[]> output,
        PtySession session,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpToSocket = PumpSubscriptionToSocketAsync(output, session, stream, linked.Token);
        var pumpToSession = PumpSocketToSessionAsync(session, stream, linked.Token);
        await Task.WhenAny(pumpToSocket, pumpToSession);
        linked.Cancel();

        try
        {
            await pumpToSocket;
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await pumpToSession;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task PumpSubscriptionToSocketAsync(
        ChannelReader<byte[]> output,
        PtySession session,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        await foreach (var chunk in output.ReadAllAsync(cancellationToken))
        {
            await stream.WriteAsync(chunk, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        await session.WaitForExitAsync(cancellationToken);
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

    private static async Task PumpInputToSocketAsync(ChannelReader<byte[]> input, NetworkStream stream, CancellationToken cancellationToken)
    {
        await foreach (var chunk in input.ReadAllAsync(cancellationToken))
        {
            await stream.WriteAsync(chunk, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }
    private static async Task PumpSessionToSocketAsync(PtySession session, NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await session.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                await session.WaitForExitAsync(cancellationToken);
                return;
            }
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private static async Task WriteResponseAsync(Stream stream, PtyDaemonResponse response, CancellationToken cancellationToken)
    {
        var json = $"{JsonSerializer.Serialize(response, PtyDaemonJsonContext.Default.PtyDaemonResponse)}\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /** Reads byte by byte so that nothing after the newline is consumed: attach/subscribe hand the same stream to the raw tunnel. */
    private static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
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
            if (bytes.Count == maxBytes)
                throw new InvalidDataException($"request line exceeds {maxBytes} bytes");
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
            Home = info.Home,
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

    /** 只有记录、没有活进程的 pty: 状态恒为 Exited, attach 会按记录里的参数复活它。 */
    private static PtyDaemonSessionDto ToDto(PtyRecord record) => new()
    {
        Id = record.Id,
        Command = string.Join(' ', new[] { record.FileName }.Concat(record.Arguments)),
        StartedAt = record.UpdatedAt,
        Status = nameof(PtySessionStatus.Exited),
        Columns = record.Columns,
        Rows = record.Rows,
        WantsMouse = record.WantsMouse,
        Home = record.Home,
        AgentSessionId = record.AgentSessionId,
    };

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

    /** 尺寸落盘的代次: 每次写(含 attach 时的同尺寸 resize)自增, 常驻 TUI 据此在 attach/尺寸变化时整帧重画。 */
    private static readonly ConcurrentDictionary<string, long> SizeGenerations = new();

    /** 会话尺寸落盘: ConPTY 子进程读不到 resize 后的窗口尺寸, 由尺寸的权威方(daemon)写文件给常驻 TUI 自己读。 */
    private void WriteSessionSize(PtySession session)
    {
        try
        {
            var info = session.ToInfo();
            var id = session.Id.ToString();
            // 代次跨 daemon 重启单调: 首个代次从落盘记录续上, 否则重启后回退到 1, 常驻 TUI 会与旧代次撞号。
            var seeded = PtyRecords.TryLoad(id, _runRoot)?.SizeGeneration ?? 0;
            var generation = SizeGenerations.AddOrUpdate(id, _ => seeded + 1, static (_, current) => current + 1);
            Directory.CreateDirectory(_runRoot ?? PtyDaemonPaths.RunDirectory());
            File.WriteAllText(SizeFile(id), $"{info.Columns} {info.Rows} {generation}");
            if (PtyRecords.TryLoad(id, _runRoot) is { } record)
            {
                record.SizeGeneration = generation;
                record.Columns = info.Columns;
                record.Rows = info.Rows;
                PtyRecords.Save(record, _runRoot);
            }
        }
        catch (IOException)
        {
        }
    }

    private string SizeFile(string id) => Path.Combine(_runRoot ?? PtyDaemonPaths.RunDirectory(), $"size-{id}");

    private void DeleteSizeFile(string id)
    {
        try
        {
            if (SizeFile(id) is { } path && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /** identify / publish-panes 上报的 agent 会话 id 写回记录: pty 退出后, 这是"继续那条会话"的入口。 */
    private void SaveAgentSessionId(string id, string? agentSessionId)
    {
        if (PtyRecords.TryLoad(id, _runRoot) is not { } record)
            return;
        record.AgentSessionId = agentSessionId;
        PtyRecords.Save(record, _runRoot);
    }

    private static PtyStartInfo StartInfoOf(PtyRecord record)
    {
        var environment = new Dictionary<string, string?>(record.Environment ?? new Dictionary<string, string?>())
        {
            [PtySessionProtocol.SessionVariable] = record.Id,
        };
        if (record.Home is { Length: > 0 } childHome)
            environment[PtySessionProtocol.ChildHomeVariable] = childHome;
        return new PtyStartInfo
        {
            FileName = record.FileName,
            Arguments = record.Arguments,
            WorkingDirectory = record.WorkingDirectory,
            Environment = environment,
            Home = record.Home,
            Rows = record.Rows == 0 ? 24 : record.Rows,
            Columns = record.Columns == 0 ? 80 : record.Columns,
            WantsMouse = record.WantsMouse,
        };
    }

    /** attach 失败时的出口: 告诉用户那条会话怎么继续, 而不是只说"没在跑"。 */
    private static string AttachRecoveryHint(string? agentSessionId)
        => agentSessionId is { Length: > 0 }
            ? $" (agent session {agentSessionId}; continue it with `dsharp tui --session {agentSessionId}` or `dsharp gui --session {agentSessionId}`)"
            : "";
}
