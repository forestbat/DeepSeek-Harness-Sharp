using System.Text;

namespace Dsh.Tests;

/**
 * 常驻 TUI 的输入旁路: 会话输入交给订阅者(daemon 的 "input" 方法), 不再写 pty。
 * 这是 Windows 上"中文引号打不进去 / Shift+Enter 没反应 / 多行粘贴变多条消息"的根因所在 ——
 * ConPTY 把输入字节翻成控制台记录时会丢东亚歧义符号、丢修饰键编码、把 ESC[200~ 吃成普通字符。
 */
public class PtyDaemonInputChannelTests
{
    private static readonly List<string> WindowsShellArguments = ["/c", "pause"];
    private static readonly List<string> UnixShellArguments = ["-c", "sleep 60"];

    private static string Shell => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static List<string> ShellArguments => OperatingSystem.IsWindows() ? WindowsShellArguments : UnixShellArguments;

    [Fact(Timeout = 30000)]
    public async Task Session_InputSubscription_TakesTheInputInsteadOfThePty()
    {
        using var host = new PtyHost();
        using var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = Shell,
            Arguments = ShellArguments,
            Rows = 24,
            Columns = 80,
        }, cancellationToken: TestContext.Current.CancellationToken);

        using var subscription = session.SubscribeInput();
        await session.WriteAsync("粘贴一\r\n"u8.ToArray(), TestContext.Current.CancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var chunk = await subscription.Reader.ReadAsync(timeout.Token);

        Assert.Equal("粘贴一\r\n", Encoding.UTF8.GetString(chunk));
    }

    [Fact(Timeout = 30000)]
    public async Task Daemon_StreamsAttachedInput_ToTheInputChannel()
    {
        var root = TempTree.CreateSocketDirectory("dshinput");
        var socketPath = Path.Combine(root, "dsh.sock");
        var portFile = OperatingSystem.IsWindows() ? Path.Combine(root, "dsh.port") : null;
        try
        {
            await using var daemon = new PtyDaemon(socketPath: socketPath, portFile: portFile);
            await daemon.StartAsync(TestContext.Current.CancellationToken);
            var started = await PtyDaemonClient.StartAsync(
                new PtyDaemonStartParams
                {
                    FileName = Shell,
                    Arguments = ShellArguments,
                    Rows = 24,
                    Columns = 80,
                },
                socketPath,
                portFile,
                TestContext.Current.CancellationToken);

            using var channel = await PtyDaemonClient.OpenInputChannelAsync(
                started.Id, socketPath, portFile, TestContext.Current.CancellationToken);

            // 代理/窗口送进来的字节就是从这里出来的: 与 Unix 的原始字节一致, 不经 pty 的 ConPTY 翻译。
            await using var proxy = await PtyDaemonClient.SubscribeAsync(started.Id, socketPath, portFile, TestContext.Current.CancellationToken);
            await proxy.WriteAsync("粘贴二\r\n"u8.ToArray(), TestContext.Current.CancellationToken);

            var buffer = new byte[64];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var read = await channel.ReadAsync(buffer, timeout.Token);

            Assert.Equal("粘贴二\r\n", Encoding.UTF8.GetString(buffer, 0, read));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }
}
