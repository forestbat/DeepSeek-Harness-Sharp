using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Text;
using System.Text.Json;

namespace Dsh.Pty;

public static class PtyDaemonClient
{
    public static Task<IReadOnlyList<PtyDaemonSessionDto>> ListAsync(CancellationToken cancellationToken = default)
        => ListAsync(PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static async Task<IReadOnlyList<PtyDaemonSessionDto>> ListAsync(string socketPath, string? portFile, CancellationToken cancellationToken = default)
    {
        using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
        using var stream = new NetworkStream(socket, ownsSocket: true);
        await WriteRequestAsync(stream, new PtyDaemonRequest { Method = "list" }, cancellationToken);
        var response = await ReadResponseAsync(stream, cancellationToken);
        return response.Sessions ?? [];
    }

    public static Task<PtyDaemonSessionDto> StartAsync(PtyDaemonStartParams parameters, CancellationToken cancellationToken = default)
        => StartAsync(parameters, PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static async Task<PtyDaemonSessionDto> StartAsync(PtyDaemonStartParams parameters, string socketPath, string? portFile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
        using var stream = new NetworkStream(socket, ownsSocket: true);
        await WriteRequestAsync(stream, new PtyDaemonRequest { Method = "start", Params = parameters }, cancellationToken);
        var response = await ReadResponseAsync(stream, cancellationToken);
        return response.Session
            ?? throw new InvalidOperationException("daemon returned no PTY session");
    }

    public static Task AttachAsync(
        string id,
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
        => AttachAsync(id, input, output, PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    /**
     * 终端形态 attach: 输入直接取自进程终端(Win 控制台流 / Unix fd 0 原始读)。
     * Windows 上再套一层鼠标报文过滤: ConPTY 的输入侧会把 X10 报文拆成"裸载荷字符", 到了会话里与用户输入不可区分。
     * Unix 没有这层, 报文原件直通会话由 TUI 还原成鼠标事件(拖动分隔线仍可用), 不做过滤。
     */
    public static async Task AttachConsoleAsync(string id, Stream output, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sizeWatch = WatchConsoleSizeAsync(id, stop.Token);
        try
        {
            var input = ConsoleInput.OpenForRead();
            if (!OperatingSystem.IsWindows())
            {
                // Unix: 报文原件直通会话, 由 TUI 自己还原鼠标事件。
                await AttachAsync(id, input, output, cancellationToken);
                return;
            }

            // Windows/ConPTY: 终端上报是 VT 字节(X10 或 SGR), ConPTY 不会翻译 X10, 所以代理在本地解析后
            // 以 MOUSE_EVENT 记录注入会话(见 MouseReportParser / WindowsConsoleMouseInjector)。
            var mouse = Channel.CreateBounded<PtyMouseEvent>(
                new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
            var forward = ForwardMouseAsync(id, mouse.Reader, cancellationToken);
            try
            {
                await AttachAsync(id, new MouseReportParsingStream(input, mouse.Writer), output, cancellationToken);
            }
            finally
            {
                mouse.Writer.TryComplete();
                await forward;
            }
        }
        finally
        {
            stop.Cancel();
            try
            {
                await sizeWatch;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /** 终端尺寸变化的比对间隔: ConPTY 的 resize 不会打断阻塞读, 只能定时比对客户端窗口尺寸。 */
    private const int ConsoleSizePollMilliseconds = 200;

    /**
     * 终端形态独有的尺寸通道。attach 只上报一次尺寸, 之后用户改窗口大小不会再来任何事件,
     * 常驻 TUI 便一直按旧尺寸画(调大留白、调小被压)。这里定时比对并上报, daemon 落盘尺寸文件后会话重排。
     * 独立窗口(GPU)形态有自己的尺寸来源(窗口 OnResize), 不走这里。
     */
    private static async Task WatchConsoleSizeAsync(string id, CancellationToken cancellationToken)
    {
        if (Console.IsOutputRedirected)
            return;
        if (!TryReadConsoleSize(out var columns, out var rows))
            return;
        while (true)
        {
            await Task.Delay(ConsoleSizePollMilliseconds, cancellationToken);
            if (!TryReadConsoleSize(out var currentColumns, out var currentRows))
                continue;
            if (currentColumns == columns && currentRows == rows)
                continue;
            (columns, rows) = (currentColumns, currentRows);
            try
            {
                await ResizeAsync(id, rows, columns, cancellationToken);
            }
            catch (Exception error) when (error is IOException or SocketException or InvalidOperationException or PtyDaemonNotRunningException)
            {
                // daemon 暂时不可用: 保留旧尺寸, 下一次轮询再试
            }
        }
    }

    private static bool TryReadConsoleSize(out int columns, out int rows)
    {
        try
        {
            columns = Console.WindowWidth;
            rows = Console.WindowHeight;
            return rows >= 4 && columns >= 20;
        }
        catch (Exception error) when (error is IOException or ArgumentOutOfRangeException)
        {
            columns = 0;
            rows = 0;
            return false;
        }
    }

    /** 把代理解析出的鼠标事件发给 daemon 注入会话; 失败只丢这一条, 绝不打断输入。 */
    private static async Task ForwardMouseAsync(string id, ChannelReader<PtyMouseEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var mouse in events.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await SendMouseAsync(id, mouse, cancellationToken);
                }
                catch (IOException)
                {
                    // daemon 暂时不在/连接断了: 丢这一条事件, 不打断输入
                }
                catch (InvalidOperationException)
                {
                    // daemon 拒绝(会话已结束): 同上
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static Task SendMouseAsync(string id, PtyMouseEvent mouse, CancellationToken cancellationToken = default)
        => SendMouseAsync(
            id,
            mouse,
            PtyDaemonPaths.SocketPath(),
            OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null,
            cancellationToken);

    public static async Task SendMouseAsync(string id, PtyMouseEvent mouse, string socketPath, string? portFile, CancellationToken cancellationToken = default)
    {
        using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
        using var stream = new NetworkStream(socket, ownsSocket: true);
        await WriteRequestAsync(
            stream,
            new PtyDaemonRequest
            {
                Method = "mouse",
                Id = id,
                MouseX = mouse.X,
                MouseY = mouse.Y,
                MouseButtonState = mouse.ButtonState,
                MouseEventFlags = mouse.EventFlags,
            },
            cancellationToken);
        await ReadResponseAsync(stream, cancellationToken);
    }

    public static async Task AttachAsync(
        string id,
        Stream input,
        Stream output,
        string socketPath,
        string? portFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        // 隧道里流的是状态重建快照(UTF-8 原样字节): 不声明代码页, 控制台会按 OEM 代码页把中文读成乱码。
        ConsoleCodePage.EnsureUtf8();
        await TryResizeToClientAsync(id, socketPath, portFile, cancellationToken);
        using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
        using var stream = new NetworkStream(socket, ownsSocket: true);
        await WriteRequestAsync(stream, new PtyDaemonRequest { Method = "attach", Id = id }, cancellationToken);
        var response = await ReadResponseAsync(stream, cancellationToken);
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "daemon attach failed");

        using var raw = PtyRawMode.TryEnable();

        // 只有吃鼠标的会话(常驻 TUI)才打开宿主终端上报: 会话启动时写的开启序列早于本次 attach, 已被
        // daemon 排空(见 DrainPendingOutput), 终端不收到它就永远不会上报。
        // 别的会话(如 shell pane)必须保持关闭 —— Unix 上代理是原样透传字节, 上报会直接敲进 shell 的命令行。
        var wantsMouse = response.Session?.WantsMouse == true;
        var initialMode = wantsMouse ? PtyConsoleBridge.MouseEnableSequence : PtyConsoleBridge.MouseDisableSequence;
        await output.WriteAsync(Encoding.UTF8.GetBytes(initialMode), cancellationToken);
        await output.FlushAsync(cancellationToken);

        try
        {
            await TunnelAsync(input, output, stream, cancellationToken);
        }
        finally
        {
            // 服务端的状态重建进入的是备用屏: 离开 attach 时还原备用屏与光标, 免得终端留在会话画面里;
            // 同时关掉鼠标上报, 否则退出 TUI 后终端里的鼠标一动就被 shell 当输入回显。
            await output.WriteAsync(
                Encoding.UTF8.GetBytes($"\u001b[?1049l\u001b[?25h{PtyConsoleBridge.MouseDisableSequence}"),
                cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

    public static Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
        => IsRunningAsync(PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static async Task<bool> IsRunningAsync(string socketPath, string? portFile, CancellationToken cancellationToken = default)
    {
        try
        {
            using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
            return true;
        }
        catch (PtyDaemonNotRunningException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        // 调用方(CLI attach)随后就要写 UTF-8 字节到自己的控制台, 先声明代码页, 免得宿主按 OEM 代码页误读。
        ConsoleCodePage.EnsureUtf8();
        if (await IsRunningAsync(cancellationToken))
            return;

        StartDaemonProcess();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsRunningAsync(cancellationToken))
                return;
            await Task.Delay(25, cancellationToken);
        }

        if (await IsRunningAsync(cancellationToken))
            return;
        throw new InvalidOperationException("dsh tui daemon failed to start");
    }

    private static void StartDaemonProcess()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("failed to resolve the current executable to start dsh tui daemon");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        startInfo.ArgumentList.Add("tui");
        startInfo.ArgumentList.Add("daemon");
        var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("failed to start dsh tui daemon");
        _ = process;
    }

    /**
     * 连接 daemon 的超时。端口文件/套接字文件可能是上次崩溃留下的, 无超时会把 CLI 卡死(实测 attach 就卡在这)。
     * 但也不能给太长: 本地 loopback 要么立刻连上、要么失败, 而**失效端口**在 Windows 上会被丢弃 SYN 重传,
     * 单次探测实测要 ~2s —— `EnsureRunningAsync` 的首次探测加上轮询里的失败探测会白等 4s 以上。
     */
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    private static async Task<Socket> ConnectAsync(string socketPath, string? portFile, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ConnectTimeout);
            if (OperatingSystem.IsWindows())
            {
                if (portFile is null)
                    throw new PtyDaemonNotRunningException();
                if (!File.Exists(portFile))
                    throw new PtyDaemonNotRunningException();
                if (!int.TryParse((await File.ReadAllTextAsync(portFile, cancellationToken)).Trim(), out var port))
                    throw new PtyDaemonNotRunningException();
                var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }

                return client.Client;
            }

            if (!File.Exists(socketPath))
                throw new PtyDaemonNotRunningException();
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            return socket;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PtyDaemonNotRunningException();
        }
        catch (SocketException)
        {
            throw new PtyDaemonNotRunningException();
        }
        catch (IOException)
        {
            throw new PtyDaemonNotRunningException();
        }
    }

    /** 上报本 pty 里常驻 TUI 的 agent 会话 id(跨进程归属)。 */
    public static Task IdentifyAsync(string id, string agentSessionId, CancellationToken cancellationToken = default)
        => IdentifyAsync(id, agentSessionId, PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static Task IdentifyAsync(string id, string agentSessionId, string socketPath, string? portFile, CancellationToken cancellationToken = default)
        => SendAsync(socketPath, portFile, new PtyDaemonRequest { Method = "identify", Id = id, AgentSessionId = agentSessionId }, cancellationToken);

    /** 发布本 pty 的窗格目录与尾行快照, 供其他进程读取。 */
    public static Task PublishPanesAsync(string id, string agentSessionId, IReadOnlyList<PtyPaneSnapshotDto> panes, CancellationToken cancellationToken = default)
        => PublishPanesAsync(id, agentSessionId, panes, PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static Task PublishPanesAsync(string id, string agentSessionId, IReadOnlyList<PtyPaneSnapshotDto> panes, string socketPath, string? portFile, CancellationToken cancellationToken = default)
        => SendAsync(socketPath, portFile, new PtyDaemonRequest { Method = "publish-panes", Id = id, AgentSessionId = agentSessionId, Panes = [.. panes] }, cancellationToken);

    /** 向目标 pty 的常驻 TUI 派发一条窗格输入(focus/text/key), 返回分配到的 seq。 */
    public static Task<long> ControlSendAsync(string id, string kind, int paneId, string payload, CancellationToken cancellationToken = default)
        => ControlSendAsync(id, kind, paneId, payload, PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static async Task<long> ControlSendAsync(string id, string kind, int paneId, string payload, string socketPath, string? portFile, CancellationToken cancellationToken = default)
        => (await SendAsync(socketPath, portFile, new PtyDaemonRequest { Method = "control-send", Id = id, Kind = kind, PaneId = paneId, Payload = payload }, cancellationToken)).Seq;

    /** 长轮询取 sinceSeq 之后的新控制消息; 无消息时 daemon 侧最长等待 30s。 */
    public static Task<IReadOnlyList<PtyControlMessageDto>> ControlReadAsync(string id, long sinceSeq, CancellationToken cancellationToken = default)
        => ControlReadAsync(id, sinceSeq, PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static async Task<IReadOnlyList<PtyControlMessageDto>> ControlReadAsync(string id, long sinceSeq, string socketPath, string? portFile, CancellationToken cancellationToken = default)
        => (await SendAsync(socketPath, portFile, new PtyDaemonRequest { Method = "control-read", Id = id, SinceSeq = sinceSeq }, cancellationToken)).Controls ?? [];

    /** 调整 daemon 里会话的尺寸(tmux 语义): attach 前按客户端终端尺寸调用, 免得画面下方留一大片空白。 */
    public static Task ResizeAsync(string id, int rows, int columns, CancellationToken cancellationToken = default)
        => ResizeAsync(id, rows, columns, PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, cancellationToken);

    public static async Task ResizeAsync(
        string id,
        int rows,
        int columns,
        string socketPath,
        string? portFile,
        CancellationToken cancellationToken = default)
    {
        using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
        using var stream = new NetworkStream(socket, ownsSocket: true);
        await WriteRequestAsync(
            stream,
            new PtyDaemonRequest
            {
                Method = "resize",
                Id = id,
                Params = new PtyDaemonStartParams { Rows = rows, Columns = columns },
            },
            cancellationToken);
        await ReadResponseAsync(stream, cancellationToken);
    }

    /** attach 前按客户端终端尺寸调整会话; 尺寸拿不到或会话已退出时静默跳过, 不影响 attach 主体。 */
    private static async Task TryResizeToClientAsync(
        string id,
        string socketPath,
        string? portFile,
        CancellationToken cancellationToken)
    {
        if (Console.IsOutputRedirected)
            return;
        try
        {
            var rows = Console.WindowHeight;
            var columns = Console.WindowWidth;
            if (rows < 4 || columns < 20)
                return;
            await ResizeAsync(id, rows, columns, socketPath, portFile, cancellationToken);
        }
        catch (Exception)
        {
            // 尺寸查询失败/会话已退出/daemon 是不认识 resize 的旧版本: 保持会话原尺寸, 不影响 attach 主体。
            // 这里不能往 stderr 写东西 —— 终端形态下它会落到会话画面上, 污染用户的界面。
        }
    }

    private static async Task<PtyDaemonResponse> SendAsync(string socketPath, string? portFile, PtyDaemonRequest request, CancellationToken cancellationToken)
    {
        using var socket = await ConnectAsync(socketPath, portFile, cancellationToken);
        using var stream = new NetworkStream(socket, ownsSocket: true);
        await WriteRequestAsync(stream, request, cancellationToken);
        return await ReadResponseAsync(stream, cancellationToken);
    }

    private static async Task WriteRequestAsync(Stream stream, PtyDaemonRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request, PtyDaemonJson.Options) + "\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<PtyDaemonResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync(stream, cancellationToken)
            ?? throw new IOException("daemon closed the connection");
        var response = JsonSerializer.Deserialize<PtyDaemonResponse>(line, PtyDaemonJson.Options)
            ?? throw new InvalidOperationException("daemon returned an invalid response");
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "daemon request failed");
        return response;
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

    private static async Task TunnelAsync(Stream input, Stream output, NetworkStream stream, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpInput = PumpInputAsync(input, stream, linked.Token);
        var pumpOutput = PumpOutputAsync(output, stream, linked.Token);
        await Task.WhenAny(pumpInput, pumpOutput);
        linked.Cancel();

        try
        {
            await pumpInput;
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await pumpOutput;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task PumpInputAsync(Stream input, NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return;
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private static async Task PumpOutputAsync(Stream output, NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var marker = Encoding.UTF8.GetBytes(PtySessionProtocol.DetachMarker);
        var window = new List<byte>(marker.Length * 2);
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                // 流结束: 把窗口里剩下的字节冲刷掉, 否则小输出会被扣住不转发。
                if (window.Count > 0)
                {
                    await output.WriteAsync(window.ToArray(), cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    window.Clear();
                }

                return;
            }

            window.AddRange(buffer.AsSpan(0, read).ToArray());
            var index = IndexOfMarker(window, marker);
            if (index >= 0)
            {
                if (index > 0)
                {
                    await output.WriteAsync(window.GetRange(0, index).ToArray(), cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                // 会话要求 proxy 离开: 结束隧道(会话本身继续跑), 调用方随即看到"已 detach"。
                return;
            }

            // 只扣住"确实是标记前缀"的尾巴(半截标记), 其余一律立即转发。
            var keep = LongestMarkerPrefixSuffix(window, marker);
            var emit = window.Count - keep;
            if (emit <= 0)
                continue;
            await output.WriteAsync(window.GetRange(0, emit).ToArray(), cancellationToken);
            await output.FlushAsync(cancellationToken);
            window.RemoveRange(0, emit);
        }
    }

    /** 窗口尾部与标记前缀匹配的最长长度: 0 表示没有半截标记, 可以全部放行。 */
    private static int LongestMarkerPrefixSuffix(List<byte> buffer, byte[] marker)
    {
        var max = Math.Min(buffer.Count, marker.Length - 1);
        for (var length = max; length > 0; length--)
        {
            var matched = true;
            for (var offset = 0; offset < length; offset++)
            {
                if (buffer[buffer.Count - length + offset] == marker[offset])
                    continue;
                matched = false;
                break;
            }

            if (matched)
                return length;
        }

        return 0;
    }

    private static int IndexOfMarker(List<byte> buffer, byte[] marker)
    {
        for (var start = 0; start + marker.Length <= buffer.Count; start++)
        {
            var matched = true;
            for (var offset = 0; offset < marker.Length; offset++)
            {
                if (buffer[start + offset] == marker[offset])
                    continue;
                matched = false;
                break;
            }

            if (matched)
                return start;
        }

        return -1;
    }
}