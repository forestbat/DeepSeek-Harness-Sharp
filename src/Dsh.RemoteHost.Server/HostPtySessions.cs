using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Dsh.Pty;

namespace Dsh.RemoteHost;

/** host 使用的 PTY daemon 端点; EnsureRunningAsync 在 daemon 不在时把它拉起。 */
public sealed record HostPtyDaemon(string SocketPath, string? PortFile, Func<CancellationToken, Task> EnsureRunningAsync)
{
    /** 与 `dsharp tui list` 同一个 daemon(LocalEndpointRoot 下的 run/dsh.sock)。 */
    public static HostPtyDaemon Default()
        => new(PtyDaemonPaths.SocketPath(), OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile() : null, PtyDaemonClient.EnsureRunningAsync);
}

/**
 * host.pty.* 的托管: PTY 由 PTY daemon 持有, 不随 host 进程或任何客户端连接结束。
 * 每个连接 attach 时各开一条 daemon 订阅(首帧是整屏快照, 多端互不抢字节);
 * host 自己再为每个 PTY 保持一条订阅, 用于写入、跟踪活动时间与退出码, 并同步到会话登记表。
 */
public sealed class HostPtySessions(HostSessionRegistry registry, HostPtyDaemon daemon, string? harnessHome) : IAsyncDisposable
{
    /** 远端终端由 GUI/Web 的 VT 控件显示: 声明 xterm 兼容真彩终端, systemd 等无终端环境起的 host 自身没有 TERM。 */
    private static readonly IReadOnlyDictionary<string, string?> TerminalEnvironment = new Dictionary<string, string?>
    {
        ["TERM"] = "xterm-256color",
        ["COLORTERM"] = "truecolor",
    };

    /** PTY 输出结束后等 daemon 记下退出码的轮询次数与间隔: 子进程退出回调可能略晚于输出 EOF。 */
    private const int ExitCodeAttempts = 20;

    private static readonly TimeSpan ExitCodeDelay = TimeSpan.FromMilliseconds(50);

    private const string ExitedStatus = nameof(PtySessionStatus.Exited);

    private readonly ConcurrentDictionary<string, Task<PtyRemoteSession>> _watchers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();


    public Task<string> StartAsync(RemotePtyStartRequest request, string workingDirectory, CancellationToken cancellationToken)
        => LaunchAsync(request, workingDirectory, HostSessionKinds.Terminal, null, request.Environment, cancellationToken);

    /** Starts an external agent's TUI; status reflects the process, not guesses from screen text. */
    public Task<string> StartAgentAsync(
        RemotePtyStartRequest request,
        string workingDirectory,
        string agentKind,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
        => LaunchAsync(request, workingDirectory, HostSessionKinds.External, agentKind, environment, cancellationToken);

    private async Task<string> LaunchAsync(
        RemotePtyStartRequest request,
        string workingDirectory,
        string kind,
        string? agentKind,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        await daemon.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        var variables = new Dictionary<string, string?>(TerminalEnvironment);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            variables[name] = value;
        var parameters = new PtyDaemonStartParams
        {
            FileName = request.FileName,
            Arguments = [.. request.Arguments],
            WorkingDirectory = workingDirectory,
            Environment = variables,
            Home = harnessHome,
        };
        parameters.Columns = request.Columns ?? parameters.Columns;
        parameters.Rows = request.Rows ?? parameters.Rows;
        var started = await PtyDaemonClient.StartAsync(parameters, daemon.SocketPath, daemon.PortFile, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        registry.Register(new RemoteSessionSummary(
            started.Id, kind, agentKind, workingDirectory, request.Title ?? started.Command, HostSessionStatuses.Running, now, now, 0, null),
            kind == HostSessionKinds.External ? new HostAgentLaunch(request.FileName, request.Arguments, environment) : null);
        await registry.FlushAsync().ConfigureAwait(false);
        await WatcherAsync(started.Id, _lifetime.Token).ConfigureAwait(false);
        return started.Id;
    }


    /** 任何连接的输入都经 host 的那条订阅写入; daemon 里不是本 host 起的 PTY 也能写。 */
    public async Task WriteAsync(string ptyId, byte[] data, CancellationToken cancellationToken)
    {
        var watcher = await WatcherAsync(ptyId, cancellationToken).ConfigureAwait(false);
        await watcher.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public Task ResizeAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken)
        => PtyDaemonClient.ResizeAsync(ptyId, rows, columns, daemon.SocketPath, daemon.PortFile, cancellationToken);

    public async Task StopAsync(string ptyId, CancellationToken cancellationToken)
    {
        await PtyDaemonClient.KillAsync(ptyId, daemon.SocketPath, daemon.PortFile, cancellationToken).ConfigureAwait(false);
        registry.MarkExited(ptyId, null);
    }

    /** 连接已有 PTY: 首帧是整屏快照, 已退出会话在快照后以 Eof 结束; 不存在则在订阅时失败。 */
    public async Task<RemoteHostStream<RemotePtyOutput>> AttachAsync(string ptyId, CancellationToken cancellationToken)
    {
        var session = await PtyDaemonClient.SubscribeAsync(ptyId, daemon.SocketPath, daemon.PortFile, cancellationToken).ConfigureAwait(false);
        return new RemoteHostStream<RemotePtyOutput>(StreamAsync(ptyId, session, cancellationToken), session.DisposeAsync);
    }

    /** host 重启后对账: 登记表里的终端与外部 Agent 若仍在 daemon 里运行就复活并重新跟踪, 否则保持 Exited。 */
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var terminals = registry.List().Where(session => session.Kind is HostSessionKinds.Terminal or HostSessionKinds.External).ToList();
        if (terminals.Count == 0 || !await PtyDaemonClient.IsRunningAsync(daemon.SocketPath, daemon.PortFile, cancellationToken).ConfigureAwait(false))
            return;
        var running = (await PtyDaemonClient.ListAsync(daemon.SocketPath, daemon.PortFile, cancellationToken).ConfigureAwait(false))
            .Where(pty => pty.Status != ExitedStatus)
            .Select(pty => pty.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var terminal in terminals.Where(terminal => running.Contains(terminal.SessionId)))
        {
            registry.SetStatus(terminal.SessionId, HostSessionStatuses.Running);
            await WatcherAsync(terminal.SessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /** host 退出只断开自己的订阅, PTY 留在 daemon 里。 */
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var watcher in _watchers.Values)
        {
            if (watcher.IsCompletedSuccessfully)
                await watcher.Result.DisposeAsync().ConfigureAwait(false);
        }
        _watchers.Clear();
        _lifetime.Dispose();
    }

    private async Task<PtyRemoteSession> WatcherAsync(string ptyId, CancellationToken cancellationToken)
    {
        var watcher = _watchers.GetOrAdd(ptyId, id => WatchAsync(id, cancellationToken));
        try
        {
            return await watcher.ConfigureAwait(false);
        }
        catch
        {
            _watchers.TryRemove(new KeyValuePair<string, Task<PtyRemoteSession>>(ptyId, watcher));
            throw;
        }
    }

    private async Task<PtyRemoteSession> WatchAsync(string ptyId, CancellationToken cancellationToken)
    {
        var session = await PtyDaemonClient.SubscribeAsync(ptyId, daemon.SocketPath, daemon.PortFile, cancellationToken).ConfigureAwait(false);
        _ = FollowAsync(ptyId, session);
        return session;
    }

    /** 输出即活动; 订阅结束即 PTY 退出(host 自己退出时除外), 取 daemon 记下的退出码。 */
    private async Task FollowAsync(string ptyId, PtyRemoteSession session)
    {
        try
        {
            await foreach (var _ in session.Output.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                registry.Touch(ptyId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _watchers.TryRemove(ptyId, out _);
        await session.DisposeAsync().ConfigureAwait(false);
        if (!_lifetime.IsCancellationRequested)
            registry.MarkExited(ptyId, await ExitCodeAsync(ptyId).ConfigureAwait(false));
    }

    private async Task<int?> ExitCodeAsync(string ptyId)
    {
        for (var attempt = 0; attempt < ExitCodeAttempts; attempt++)
        {
            try
            {
                var pty = (await PtyDaemonClient.ListAsync(daemon.SocketPath, daemon.PortFile, _lifetime.Token).ConfigureAwait(false))
                    .FirstOrDefault(candidate => candidate.Id == ptyId);
                if (pty is null || pty.Status == ExitedStatus)
                    return pty?.ExitCode;
                await Task.Delay(ExitCodeDelay, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or PtyDaemonNotRunningException or OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    private static async IAsyncEnumerable<RemotePtyOutput> StreamAsync(string ptyId, PtyRemoteSession session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in session.Output.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return new RemotePtyOutput(ptyId, Convert.ToBase64String(chunk), false);
        yield return new RemotePtyOutput(ptyId, "", true);
    }
}

