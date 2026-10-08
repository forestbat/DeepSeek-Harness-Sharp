using System.Text;
using Dsh.Pty;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * GPU 形态语义: 关窗口必须等于 detach —— proxy 退出, 但 daemon 里的会话继续 Running。
 * 用隐藏窗口跑真实的 GpuSessionProxy(与 `dsh tui --gpu` 同一条路径), 再 RequestClose 模拟关窗口。
 */
[Collection(GuiSerialCollection.CollectionName)]
[Trait("Category", "OnlyGpu")]
public sealed class GpuSessionProxyDetachTests
{
    [Fact]
    public async Task Closing_Gpu_Window_Keeps_Session_Running()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await EnsureDaemonAsync(cancellationToken);

        var shell = PtyShell.Resolve();
        var session = await PtyDaemonClient.StartAsync(
            new PtyDaemonStartParams
            {
                FileName = shell,
                Arguments = [.. PtyShell.Arguments(shell)],
                Rows = 24,
                Columns = 80,
            },
            cancellationToken);
        try
        {
            var atlas = GlyphAtlas.Shared;
            using var windowHost = CreateGpuHostOrSkip(atlas);
            var proxy = new GpuSessionProxy(session.Id, atlas, windowHost);
            // GL 上下文与创建它的线程绑定: 宿主循环必须在同一线程跑, 关窗口从定时器线程触发。
            using var closer = new Timer(_ => windowHost.RequestClose(), null, 2500, Timeout.Infinite);
            proxy.Run();
            proxy.Dispose();

            // 关窗口之后: proxy 已退出, 会话必须还在(否则"关窗口=detach"就是空话)
            var status = (await PtyDaemonClient.ListAsync(cancellationToken: cancellationToken))
                .FirstOrDefault(candidate => candidate.Id == session.Id)
                ?.Status;
            Assert.Equal("Running", status);
        }
        finally
        {
            try
            {
                using var keys = new MemoryStream(Encoding.ASCII.GetBytes("exit\r"));
                await PtyDaemonClient.AttachAsync(session.Id, keys, Stream.Null, cancellationToken);
            }
            catch (Exception)
            {
                // 会话已自行退出
            }
        }
    }

    /**
     * 会话自己结束(/exit、两次 Ctrl+C、进程退出)时窗口必须自动关掉, 而不是留下一个黑框让用户手动关。
     * 旧实现只在 `_tunnel.IsCancellationRequested` 时返回"该关窗口", 而那个标志只有窗口关闭后才置位(鸡生蛋)。
     */
    [Fact]
    public async Task Session_Exit_Closes_Window_Automatically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await EnsureDaemonAsync(cancellationToken);

        // 起一个稍后自己退出的会话(必须活过 attach, 否则测不到"会话结束后隧道收口"这条路径)。
        // 之后没有任何人关窗口。
        var session = await PtyDaemonClient.StartAsync(
            new PtyDaemonStartParams
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 4 127.0.0.1 > nul"]
                    : ["-c", "sleep 3"],
                Rows = 24,
                Columns = 80,
            },
            cancellationToken);
        await Task.Delay(500, cancellationToken);

        var atlas = GlyphAtlas.Shared;
        using var windowHost = CreateGpuHostOrSkip(atlas);
        var proxy = new GpuSessionProxy(session.Id, atlas, windowHost);
        // 兜底关窗: 万一回归了也别把用例挂死, 只是断言会失败并说明是兜底关的。
        var safety = new Timer(_ => windowHost.RequestClose(), null, 15000, Timeout.Infinite);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        proxy.Run();
        watch.Stop();
        proxy.Dispose();
        safety.Dispose();

        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(10),
            $"会话结束后 GPU 窗口没有自动关闭(耗时 {watch.Elapsed.TotalSeconds:F1}s, 靠兜底才关)");
    }

    /** 用 CLI 拉起 daemon: 测试进程自己调 EnsureRunningAsync 会去 spawn Dsh.Tests.exe, 起不来。 */
    /** 三种宿主(窗口/GBM/终端贴图)在当前环境都不可用时不算回归: 守卫环境, 不验语义。 */
    private static IGlSurfaceHostRunner CreateGpuHostOrSkip(GlyphAtlas atlas)
    {
        try
        {
            return GpuHostFactory.CreateWindowHost(atlas, hidden: true);
        }
        catch (InvalidOperationException error)
        {
            Assert.Skip($"无可用 GPU 宿主: {error.Message}");
            throw;
        }
    }

    private static async Task EnsureDaemonAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await PtyDaemonClient.IsRunningAsync(cancellationToken))
                return;
        }
        catch (Exception)
        {
            // 未运行: 走下面的 CLI 拉起
        }

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md")))
            root = root.Parent;
        var name = OperatingSystem.IsWindows() ? "DeepSeek-Harness-Sharp.exe" : "DeepSeek-Harness-Sharp";
        var executable = Path.Combine(
            root?.FullName ?? AppContext.BaseDirectory,
            "DeepSeek-Harness-Sharp",
            "bin",
            "Debug",
            "net10.0",
            name);

        var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("tui");
        startInfo.ArgumentList.Add("list");
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
            return;
        // 必须持续读取重定向的流, 否则管道缓冲区写满会死锁; 退出不确定时连子进程树一起杀掉, 避免残留进程占住句柄。
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            _ = await output;
            _ = await error;
        }
    }
}
