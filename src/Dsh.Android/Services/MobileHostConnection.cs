using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dsh.Gui.Shared.Mobile;
using Dsh.RemoteHost;
using Dsh.Transport;
using Renci.SshNet;

namespace Dsh.Android.Services;

public enum MobileConnectionState
{
    Offline,
    Connecting,
    Connected,
    Reconnecting,
}

/**
 * 手机到桌面 host 的一条链路: 只负责连接、心跳与重连, 会话语义全部由 RemoteHostClient 提供。
 * 传输可走局域网 TCP + 握手令牌, 或 SSH exec 通道里跑 `dsharp host serve --stdio`;
 * 订阅复用 RemoteHostSubscriptions, 因此断线后的续订从最后一条事件之后继续。
 */
public sealed class MobileHostConnection : IMachineLink, IAgentMachineLink
{
    private const int TcpConnectTimeoutSeconds = 8;
    private const int SshConnectTimeoutSeconds = 20;
    private const int HeartbeatSeconds = 15;
    private const int HeartbeatTimeoutSeconds = 10;
    private const int MaxBackoffMilliseconds = 10_000;

    private readonly HostProfile _profile;
    private readonly string? _privateKeyPem;
    private readonly Action<string> _trustFingerprint;
    private string _fingerprint;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RemoteSessionSummary> _sessions = new(StringComparer.Ordinal);
    private readonly RemoteHostSubscriptions _subscriptions;
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<HostLease> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RemotePendingList _pending = new([], []);
    private Task? _loop;

    public MobileHostConnection(HostProfile profile, string? privateKeyPem, Action<string> trustFingerprint)
    {
        _profile = profile;
        _privateKeyPem = privateKeyPem;
        _trustFingerprint = trustFingerprint;
        _fingerprint = profile.SshFingerprint;
        _subscriptions = new RemoteHostSubscriptions(WaitConnectedAsync, _lifetime.Token);
    }

    public MobileConnectionState State { get; private set; } = MobileConnectionState.Offline;

    public string Name => _profile.Name;
    public string Target => _profile.Display;
    public HostProfile Profile => _profile;

    public string? Detail { get; private set; }

    public HostInfo? Info { get; private set; }

    public event Action? Changed;

    public IReadOnlyList<RemoteSessionSummary> Sessions
    {
        get
        {
            lock (_gate)
                return [.. _sessions.Values];
        }
    }

    public RemotePendingList Pending
    {
        get
        {
            lock (_gate)
                return _pending;
        }
    }

    public void Start() => _loop ??= RunAsync(_lifetime.Token);

    public Task<HostLease> WaitConnectedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return _connected.Task.WaitAsync(cancellationToken);
    }

    public IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
        => _subscriptions.SubscribeAsync(sessionId, fromSeq, cancellationToken);

    // ---- agent 眼里的这台机器: 与界面共用同一条已连好的链路 ----

    public bool IsConnected => State == MobileConnectionState.Connected;

    string? IAgentMachineLink.Detail => Detail;

    public async Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
    {
        var lease = await WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
        var directory = cwd.Length > 0 ? cwd : Info?.HomeRoot ?? "";
        return await lease.Client.CreateSessionAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        var lease = await WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
        // 与界面一致: 先恢复会话, agent 已退出时消息才不会丢。
        await lease.Client.ResumeSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await lease.Client.SendMessageAsync(sessionId, text, [], cancellationToken).ConfigureAwait(false);
    }

    public async Task InterruptAsync(string sessionId, CancellationToken cancellationToken)
    {
        var lease = await WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
        await lease.Client.InterruptAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
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
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var everConnected = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(everConnected ? MobileConnectionState.Reconnecting : MobileConnectionState.Connecting, Detail);
            try
            {
                await ServeOnceAsync(cancellationToken).ConfigureAwait(false);
                everConnected = true;
                failures = 0;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                failures++;
                SetState(everConnected ? MobileConnectionState.Reconnecting : MobileConnectionState.Offline, error.Message);
            }

            if (!await DelayAsync(Backoff(failures), cancellationToken).ConfigureAwait(false))
                break;
        }

        SetState(MobileConnectionState.Offline, Detail);
    }

    /** 一条链路从建立到断开; 返回即代表该链路已结束, 由外层按退避重连。 */
    private async Task ServeOnceAsync(CancellationToken cancellationToken)
    {
        await using var stream = await OpenStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var client = await RemoteHostClient.ConnectAsync(stream, _profile.Token, "dsh-android", cancellationToken).ConfigureAwait(false);
        Info = await client.InfoAsync(cancellationToken).ConfigureAwait(false);
        using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumps = StartProjections(client, link.Token);
        Publish(new HostLease(client, link.Token, closed.Task));
        SetState(MobileConnectionState.Connected, null);
        var watch = WatchAsync(client, link.Token);
        try
        {
            await Task.WhenAny([.. pumps, watch]).ConfigureAwait(false);
        }
        finally
        {
            Unpublish();
            await link.CancelAsync().ConfigureAwait(false);
            closed.TrySetResult();
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
    }

    private Task<Stream> OpenStreamAsync(CancellationToken cancellationToken)
        => _profile.Transport == HostTransportKind.Ssh
            ? OpenSshStreamAsync(cancellationToken)
            : OpenTcpStreamAsync(cancellationToken);

    private async Task<Stream> OpenTcpStreamAsync(CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(TcpConnectTimeoutSeconds));
            await tcp.ConnectAsync(_profile.Host, _profile.Port, attempt.Token).ConfigureAwait(false);
            return new NetworkStream(tcp.Client, ownsSocket: true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /**
     * SSH 通道: 读远端 daemon 的端口与本地端点令牌, 用 SSH 端口转发连到它的 loopback 监听。
     * 不走 sshd 的 stdio 桥(实测在 Windows sshd 下 stdin 拿不到数据), 且不需要在远端额外起进程。
     */
    private async Task<Stream> OpenSshStreamAsync(CancellationToken cancellationToken)
    {
        if (_privateKeyPem is not { Length: > 0 } pem)
            throw new InvalidOperationException("缺少 SSH 私钥，请先在本机生成公钥并加入桌面机");
        var keyFile = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(pem)));
        var auth = new PrivateKeyAuthenticationMethod(_profile.SshUser, keyFile);
        var connectionInfo = new ConnectionInfo(_profile.Host, _profile.Port, _profile.SshUser, auth)
        {
            Timeout = TimeSpan.FromSeconds(SshConnectTimeoutSeconds),
        };
        var client = new SshClient(connectionInfo);
        var mismatch = "";
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = Convert.ToBase64String(SHA256.HashData(e.HostKey));
            if (_fingerprint.Length == 0)
            {
                _fingerprint = fingerprint;
                _trustFingerprint(fingerprint);
                return;
            }
            if (!string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal))
            {
                mismatch = $"SSH 主机密钥不匹配(期望 {_fingerprint}, 实际 {fingerprint})";
                e.CanTrust = false;
            }
        };
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(SshConnectTimeoutSeconds));
            await client.ConnectAsync(attempt.Token).ConfigureAwait(false);
            if (mismatch.Length > 0)
                throw new IOException(mismatch);
            var portText = await ReadRemoteAsync(client, "%USERPROFILE%\\.dsh\\run\\host.port", cancellationToken).ConfigureAwait(false);
            if (!int.TryParse(portText.Trim(), out var daemonPort))
                throw new IOException("远端 dsharp daemon 未运行（缺少 host.port），请先在桌面机运行 dsharp host serve");
            var localToken = (await ReadRemoteAsync(client, "%USERPROFILE%\\.dsh\\run\\host.token", cancellationToken).ConfigureAwait(false)).Trim();
            if (localToken.Length == 0)
                throw new IOException("远端 dsharp daemon 没有提供本地访问令牌");
            var forward = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)daemonPort);
            client.AddForwardedPort(forward);
            forward.Start();
            await DiagnoseChannelsAsync(client, cancellationToken).ConfigureAwait(false);
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(TimeSpan.FromSeconds(TcpConnectTimeoutSeconds));
                await tcp.ConnectAsync(IPAddress.Loopback, (int)forward.BoundPort, connect.Token).ConfigureAwait(false);
                var stream = tcp.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(localToken), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return new SshTunnelStream(client, forward, tcp, stream);
            }
            catch
            {
                tcp.Dispose();
                forward.Stop();
                throw;
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<string> ReadRemoteAsync(SshClient client, string file, CancellationToken cancellationToken)
    {
        using var command = client.CreateCommand($"cmd /c type \"{file}\"");
        await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return command.Result;
    }

    /**
     * 对照两种 SSH 通道搬同一份数据的耗时。
     * 手机加载历史慢, 但桌面跨机同样走 SSH 却不慢: 差别在实现——桌面用原生 ssh 进程, 手机用 SSH.NET 的端口转发。
     * 用同一个连接分别量一次, 才能确定慢的是协议还是这个库的转发通道。
     */
    /**
     * 在手机上单独量 JSON 解析成本, 不经过网络。
     * 桌面同样数据只要几十毫秒, 手机上却报一秒; 只有用同一份数据把"解压/建文档/造对象"分开量,
     * 才知道差在哪一段, 而不是把原因归给"手机 CPU 慢"。
     */
    private static async Task DiagnoseChannelsAsync(SshClient client, CancellationToken cancellationToken)
    {        const int bytes = 4 * 1024 * 1024;
        try
        {
            // exec 通道: 远端把固定大小的内容写到 stdout。
            var execClock = System.Diagnostics.Stopwatch.StartNew();
            using var command = client.CreateCommand(
                $"cmd /c \"fsutil file createnew %TEMP%\\dsh-bench.bin {bytes} >nul 2>&1 & type %TEMP%\\dsh-bench.bin\"");
            var readTask = command.ExecuteAsync(cancellationToken);
            var buffer = new byte[81920];
            var total = 0L;
            var stream = command.OutputStream;
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            while (read > 0)
            {
                total += read;
                read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            await readTask.ConfigureAwait(false);
            MobileLog.Info($"channel exec: bytes={total} cost={execClock.ElapsedMilliseconds}ms rate={(total / 1024.0 / Math.Max(1, execClock.ElapsedMilliseconds)):F0}KB/s");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            MobileLog.Info($"channel exec failed: {error.Message}");
        }
        await DiagnoseExecDuplexAsync(client, cancellationToken).ConfigureAwait(false);
    }

    /**
     * exec 通道能不能双向: 远端跑一个把 stdin 原样回显的命令, 写完立刻读。
     * 上行的只有几十字节请求, 下行的才是大块历史, 所以"不对称"本身不是问题 —— 关键是上行到底通不通。
     */
    private static async Task DiagnoseExecDuplexAsync(SshClient client, CancellationToken cancellationToken)
    {
        try
        {
            // findstr 会把读到的每一行原样打回 stdout, 是最简单的回显器。
            using var command = client.CreateCommand("findstr /R \".*\"");
            var run = command.ExecuteAsync(cancellationToken);
            // 取输入流必须紧跟在启动之后: 中间让出执行上下文, 命令就不算"在执行中"了。
            var input = command.CreateInputStream();
            await input.WriteAsync(Encoding.UTF8.GetBytes("dsh-probe\r\n"), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
            await input.DisposeAsync().ConfigureAwait(false);
            await run.ConfigureAwait(false);
            MobileLog.Info($"exec duplex: exit={command.ExitStatus} result='{command.Result.Trim()}'");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            MobileLog.Info($"exec duplex failed: {error.Message}");
        }

    }

    /**
     * 经 exec 通道跑宿主(stdio 桥)取同样的历史。
     * 端口转发实测只有几百 KB/s, 而同一个库的 exec 通道有数 MB/s: 这条对比决定要不要换通道。
     */
    private static async Task DiagnoseExecHostAsync(SshClient client, CancellationToken cancellationToken)
    {
        SshCommand? command = null;
        try
        {
            var line = (await ReadTextFileAsync(BenchCommandFile, cancellationToken).ConfigureAwait(false)).Trim();
            if (line.Length == 0)
                return;
            // 宿主要先读一个字节才决定走桥接还是 LSP; 那个字节就是握手帧的第一个字节,
            // 补一个额外的字节会让帧头错位, 桥接侧解析必然失败。token 由 daemon 校验, 不给会被直接断开。
            command = client.CreateCommand(line);
            var run = command.ExecuteAsync(cancellationToken);
            var input = command.CreateInputStream();
            var directToken = (await ReadTextFileAsync(BenchTokenFile, cancellationToken).ConfigureAwait(false)).Trim();
            await using var host = await RemoteHostClient.ConnectAsync(new CommandDuplexStream(command, input), directToken.Length == 0 ? null : directToken, "dsh-android-exec", cancellationToken).ConfigureAwait(false);
            var sessions = await host.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
            MobileLog.Info($"exec host: connected, sessions={sessions.Count}");
            if (sessions.Count == 0)
                return;
            // 逐级加大请求量: 先确认小载荷稳定, 再看从哪一级开始断, 而不是一上来就撞大请求。
            foreach (var want in new[] { 10, 100, 500, 1000, 3000 })
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var page = await host.GetSessionSnapshotRangeAsync(sessions[0].SessionId, 0, want, cancellationToken).ConfigureAwait(false);
                    MobileLog.Info($"exec host: want={want} got={page.Events.Count} cost={clock.ElapsedMilliseconds}ms rate={(page.Events.Count * 1000.0 / Math.Max(1, clock.ElapsedMilliseconds)):F0}/s");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // 断开发生在第一个请求之后, 需要区分"空闲被回收"还是"首个响应后通道就关了":
                    // 先 ping 一次, 通了说明只是慢, 不通说明通道已经不在了。
                    var pingClock = System.Diagnostics.Stopwatch.StartNew();
                    var pingNote = "";
                    try
                    {
                        await host.PingAsync(cancellationToken).ConfigureAwait(false);
                        pingNote = $"ping-after-ok {pingClock.ElapsedMilliseconds}ms";
                    }
                    catch (Exception pingError) when (pingError is not OperationCanceledException)
                    {
                        pingNote = $"ping-after-fail {pingClock.ElapsedMilliseconds}ms: {pingError.Message}";
                    }
                    MobileLog.Info($"exec host: want={want} failed after {clock.ElapsedMilliseconds}ms: {error.Message}; {pingNote}");
                    break;
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // 宿主起不来时常把原因写在标准错误里, 只看到"连接已关闭"无法定位。
            MobileLog.Info($"exec host failed: {error.Message} stderr='{command?.Error?.Trim()}'");
        }
        finally
        {
            command?.Dispose();
        }
    }

    /**
     * 只启动、不通信, 看远端进程是否保持运行。
     * 立刻退出说明命令本身跑不起来(路径、参数、环境), 与协议无关; 这样能把"命令没起来"和"通道断了"分开。
     */
    private static async Task DiagnoseCommandStartAsync(SshClient client, string line, CancellationToken cancellationToken)
    {
        SshCommand? probe = null;
        try
        {
            probe = client.CreateCommand(line);
            var probeRun = probe.ExecuteAsync(cancellationToken);
            var probeInput = probe.CreateInputStream();
            await probeInput.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
            await probeInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            var finished = await Task.WhenAny(probeRun, Task.Delay(3000, cancellationToken)).ConfigureAwait(false);
            if (ReferenceEquals(finished, probeRun))
            {
                // 只写一个字节就结束, 说明命令没读输入就退出了; 把能拿到的信息全打出来。
                var tail = "";
                try
                {
                    tail = new StreamReader(probe.ExtendedOutputStream).ReadToEnd();
                }
                catch (Exception error)
                {
                    tail = error.Message;
                }
                MobileLog.Info($"exec probe: exited early completed={probeRun.IsCompleted} status={probe.ExitStatus} stderr='{probe.Error.Trim()}' ext='{tail.Trim()}'");
            }
            else
            {
                MobileLog.Info("exec probe: still running after 3s (command starts fine)");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            MobileLog.Info($"exec probe failed: {error.Message}");
        }
        finally
        {
            probe?.Dispose();
        }
    }

    /** exec 通道的读(stdout)与写(stdin)合成一个双向流。 */
    private sealed class CommandDuplexStream(SshCommand command, Stream input) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => command.OutputStream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => command.OutputStream.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => input.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => input.WriteAsync(buffer, cancellationToken);
        public override void Flush() => input.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => input.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                input.Dispose();
            base.Dispose(disposing);
        }
    }

    /** exec 通道要跑的命令; 文件不存在就不测, 免得把远端路径写死在代码里。 */
    private static string BenchCommandFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal), "..", "dsh-bench-command.txt");

    /** 直连/桥接用的访问令牌; 桌面守护进程在校验通过前不接受任何请求。 */
    private static string BenchTokenFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal), "..", "dsh-bench-token.txt");

    /**
     * 直连一个局域网监听的宿主, 量同样的历史取数有多快。
     * 这条链路只经一层网络, 没有 SSH 端口转发; 与走隧道的耗时放在一起, 就能确定隧道占了多少。
     */
    private static async Task DiagnoseDirectAsync(CancellationToken cancellationToken)
    {
        var raw = (await ReadTextFileAsync(BenchEndpointFile, cancellationToken).ConfigureAwait(false)).Replace("\r", "");
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        MobileLog.Info($"direct bench: file={BenchEndpointFile} lines={lines.Length}");
        if (lines.Length < 2)
            return;
        try
        {
            var parts = lines[0].Trim().Split(':');
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(parts[0], int.Parse(parts[1]), cancellationToken).ConfigureAwait(false);
            var stream = tcp.GetStream();
            await using var direct = await RemoteHostClient.ConnectAsync(stream, lines[1].Trim(), "dsh-android-bench", cancellationToken).ConfigureAwait(false);
            var sessions = await direct.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
            MobileLog.Info($"direct: connected, sessions={sessions.Count}");
            if (sessions.Count == 0)
                return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var page = await direct.GetSessionSnapshotRangeAsync(sessions[0].SessionId, 0, 3000, cancellationToken).ConfigureAwait(false);
            var first = clock.ElapsedMilliseconds;
            // 再取同样一段: 若服务端每次都要把整份日志重新解析一遍, 第二次的耗时会和第一次一样。
            clock.Restart();
            var again = await direct.GetSessionSnapshotRangeAsync(sessions[0].SessionId, 0, 3000, cancellationToken).ConfigureAwait(false);
            var second = clock.ElapsedMilliseconds;
            // 换一小段再取一次: 小请求的耗时就是"固定成本", 与要多少条无关。
            clock.Restart();
            var small = await direct.GetSessionSnapshotRangeAsync(sessions[0].SessionId, 0, 10, cancellationToken).ConfigureAwait(false);
            var smallCost = clock.ElapsedMilliseconds;
            MobileLog.Info($"direct: {page.Events.Count} events in {first}ms, again {again.Events.Count} in {second}ms, small {small.Events.Count} in {smallCost}ms");
            // 连续取多页, 用总字节数算真实吞吐: 单次请求的固定开销会被摊薄, 剩下的就是链路带宽。
            var totalBytes = 0L;
            var totalEvents = 0;
            var wall = System.Diagnostics.Stopwatch.StartNew();
            for (var round = 0; round < 8; round += 1)
            {
                var chunk = await direct.GetSessionSnapshotRangeAsync(sessions[0].SessionId, round * 2000, 2000, cancellationToken).ConfigureAwait(false);
                totalEvents += chunk.Events.Count;
                totalBytes += chunk.Events.Sum(item =>
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(item, Dsh.RemoteHost.HostProtocolJsonContext.Default.SessionEvent).Length);
                if (chunk.Events.Count == 0)
                    break;
            }
            var seconds = wall.ElapsedMilliseconds / 1000.0;
            MobileLog.Info($"direct: {totalEvents} events {totalBytes / 1024}KB in {wall.ElapsedMilliseconds}ms = {(totalBytes / 1024.0 / 1024.0 / Math.Max(0.001, seconds)):F2}MB/s");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            MobileLog.Info($"direct bench failed: {error.Message}");
        }
    }

    /** 直连测试的目标与令牌; 文件不存在就不测, 免得把测试地址写死在代码里。 */
    private static string BenchEndpointFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal), "..", "dsh-bench-endpoint.txt");

    private static async Task<string> ReadTextFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private Task[] StartProjections(RemoteHostClient client, CancellationToken cancellationToken)
    {
        var hasRegistry = client.Supports(HostProtocol.SessionRegistryCapability);
        var hasPending = client.Supports(HostProtocol.PendingBroadcastCapability);
        if (!hasRegistry || !hasPending)
            throw new NotSupportedException("桌面 host 版本过旧: 缺少会话登记表或待决广播能力");
        return
        [
            PumpAsync(client.PendingChangesAsync(cancellationToken), change => ReplacePending(change.Pending)),
            PumpAsync(client.HostSessionChangesAsync(cancellationToken), ApplySession),
        ];
    }

    private static async Task PumpAsync<T>(IAsyncEnumerable<T> source, Action<T> apply)
    {
        try
        {
            await foreach (var item in source.ConfigureAwait(false))
                apply(item);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or JsonRpcException)
        {
        }
    }

    /** 心跳失败即结束当前链路; TCP 半开时只有主动探测能发现。 */
    private async Task WatchAsync(RemoteHostClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(HeartbeatTimeoutSeconds));
            try
            {
                await client.PingAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or JsonRpcException)
            {
                return;
            }
        }
    }

    private void Publish(HostLease lease)
    {
        lock (_gate)
            _connected.TrySetResult(lease);
    }

    private void Unpublish()
    {
        lock (_gate)
        {
            if (_connected.Task.IsCompleted)
                _connected = new TaskCompletionSource<HostLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void ReplacePending(RemotePendingList pending)
    {
        lock (_gate)
            _pending = pending;
        Changed?.Invoke();
    }

    private void ApplySession(RemoteSessionChange change)
    {
        lock (_gate)
        {
            if (change.Snapshot is { } snapshot)
            {
                _sessions.Clear();
                foreach (var session in snapshot)
                    _sessions[session.SessionId] = session;
            }
            if (change.Session is { } updated)
            {
                if (updated.Deleted)
                    _sessions.Remove(updated.SessionId);
                else
                    _sessions[updated.SessionId] = updated;
            }
        }
        Changed?.Invoke();
    }

    private void SetState(MobileConnectionState state, string? detail)
    {
        State = state;
        Detail = detail;
        Changed?.Invoke();
    }

    private static TimeSpan Backoff(int failures)
        => TimeSpan.FromMilliseconds(Math.Min(MaxBackoffMilliseconds, 500 * Math.Pow(2, Math.Min(failures, 5))));

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /** 一条经 SSH 端口转发到远端 daemon 的 TCP 流; 释放时一并停掉转发并断开 SSH。 */
    private sealed class SshTunnelStream(SshClient client, ForwardedPortLocal forward, TcpClient tcp, Stream stream) : Stream
    {
        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => stream.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => stream.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => stream.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => stream.WriteAsync(buffer, cancellationToken);

        public override void Flush() => stream.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => stream.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                CloseAll();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            CloseAll();
            return base.DisposeAsync();
        }

        private void CloseAll()
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
            }
            try
            {
                tcp.Dispose();
            }
            catch (Exception error) when (error is ObjectDisposedException)
            {
            }
            try
            {
                forward.Stop();
                forward.Dispose();
            }
            catch (Exception error) when (error is ObjectDisposedException or Renci.SshNet.Common.SshException)
            {
            }
            client.Dispose();
        }
    }
}
