using Dsh.Host;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 不涉及 PTY 的用例: 指向一个不会被创建的端点, 一旦被用到就失败, 而不是去拉起真实的 `dsharp tui daemon`。 */
internal static class HostPtyDaemonForTests
{
    public static HostPtyDaemon Unused => new(
        "unused-pty-daemon.sock",
        null,
        _ => throw new InvalidOperationException("this test does not use PTYs"));
}

/**
 * 真实的远端 host: 真实插件组合的 harness 后端 + RemoteHostServer + loopback 监听, PTY 交给进程内的真实 PTY daemon。
 * 客户端经 RemoteHostListener.ConnectAsync 连入(与 stdio 桥接到常驻 host 是同一条路径)。
 */
internal sealed class HarnessHostFixture : IAsyncDisposable
{
    private readonly PtyDaemon _ptyDaemon;
    private readonly List<RemoteHostClient> _clients = [];
    private HarnessRemoteHostBackend? _backend;
    private readonly Func<IRemoteHostBackend, IRemoteHostBackend>? _decorateBackend;
    private CancellationTokenSource? _serving;
    private Task? _listener;

    private HarnessHostFixture(string root, PtyDaemon ptyDaemon, HostPtyDaemon endpoint, GuiTestEnvironment environment,
        Func<IRemoteHostBackend, IRemoteHostBackend>? decorateBackend)
    {
        Root = root;
        _ptyDaemon = ptyDaemon;
        PtyEndpoint = endpoint;
        Environment = environment;
        _decorateBackend = decorateBackend;
    }

    /** host.sock 与 PTY daemon 的 dsh.sock 都在这个浅目录下(套接字路径有 108 字符上限)。 */
    public string Root { get; }

    public HostPtyDaemon PtyEndpoint { get; }

    public GuiTestEnvironment Environment { get; private set; }

    public HarnessRemoteHostBackend Backend => _backend ?? throw new InvalidOperationException("host is not running");

    public static async Task<HarnessHostFixture> CreateAsync(string prefix, CancellationToken cancellationToken,
        Func<IRemoteHostBackend, IRemoteHostBackend>? decorateBackend = null)
    {
        var root = TempTree.CreateSocketDirectory(prefix);
        var socketPath = Path.Combine(root, "dsh.sock");
        var portFile = OperatingSystem.IsWindows() ? Path.Combine(root, "dsh.port") : null;
        var ptyDaemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
        await ptyDaemon.StartAsync(cancellationToken);
        var endpoint = new HostPtyDaemon(socketPath, portFile, _ => Task.CompletedTask);
        var fixture = new HarnessHostFixture(root, ptyDaemon, endpoint, await GuiTestEnvironment.CreateIsolatedAsync(), decorateBackend);
        await fixture.StartHostAsync(cancellationToken);
        return fixture;
    }

    public async Task<RemoteHostClient> ConnectAsync(string clientName, CancellationToken cancellationToken)
    {
        var stream = await RemoteHostConnector.ConnectAsync(Root, cancellationToken);
        var client = await RemoteHostClient.ConnectAsync(stream, null, clientName, cancellationToken);
        _clients.Add(client);
        return client;
    }

    /**
     * host.file.* and host.git.* reach only the home directories and session projects (HostPathPolicy).
     * Starts a shell in Project, as a session there would, so that a test directory outside the user's home (the Windows CI checkout on D:) is reachable.
     */
    public static async Task OpenProjectAsync(IWorkspaceHost client, string project, CancellationToken cancellationToken)
    {
        var (fileName, arguments) = RemoteHostMultiClientTests.Shell;
        await client.Pty.StartPtyAsync(new RemotePtyStartRequest(fileName, arguments, Cwd: project), cancellationToken);
    }

    /** 模拟 host 进程重启: 停监听、释放后端, 应用按同一个 home 重新组合后再起 host; PTY daemon 保持运行。 */
    public async Task RestartHostAsync(CancellationToken cancellationToken)
    {
        await StopHostAsync();
        Environment = await Environment.RestartAsync();
        await StartHostAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        await StopHostAsync();
        await Environment.DisposeAsync();
        await _ptyDaemon.DisposeAsync();
        TempTree.Delete(Root);
    }

    /** Starts the host on the current application (after StopHostAsync, the same live sessions are served again). */
    public async Task StartHostAsync(CancellationToken cancellationToken)
    {
        _backend = await HarnessRemoteHostBackend.CreateAsync(Environment.App, PtyEndpoint, cancellationToken);
        var server = new RemoteHostServer(new RemoteHostServerOptions(), _decorateBackend?.Invoke(_backend) ?? _backend);
        _serving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var serving = _serving.Token;
        _listener = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(server, Root, serving), cancellationToken);
    }

    /** Stops the listener and the backend; connected clients lose their connection while the application and its sessions stay alive. */
    public async Task StopHostAsync()
    {
        if (_serving is null || _listener is null || _backend is null)
            return;
        await _serving.CancelAsync();
        try
        {
            await _listener;
        }
        catch (OperationCanceledException)
        {
        }
        await _backend.DisposeAsync();
        _serving.Dispose();
        (_serving, _listener, _backend) = (null, null, null);
    }
}



