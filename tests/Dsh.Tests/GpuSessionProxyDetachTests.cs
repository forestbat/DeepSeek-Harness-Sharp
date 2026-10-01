using System.Text;
using Dsh.Pty;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * GPU 形态语义: 关窗口必须等于 detach —— proxy 退出, 但 daemon 里的会话继续 Running。
 * 用隐藏窗口跑真实的 GpuSessionProxy(与 `dsh tui --gpu` 同一条路径), 再 RequestClose 模拟关窗口。
 */
[Collection(GuiSerialCollection.CollectionName)]
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
                Arguments = PtyShell.Arguments(shell),
                Rows = 24,
                Columns = 80,
            },
            cancellationToken);
        try
        {
            var atlas = GlyphAtlas.Shared;
            using (var windowHost = GpuHostFactory.CreateWindowHost(atlas, hidden: true))
            using (var proxy = new GpuSessionProxy(session.Id, atlas, windowHost))
            {
                var running = Task.Run(proxy.Run, cancellationToken);
                await Task.Delay(2500, cancellationToken);
                windowHost.RequestClose();
                await running.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

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

    /** 用 CLI 拉起 daemon: 测试进程自己调 EnsureRunningAsync 会去 spawn Dsh.Tests.exe, 起不来。 */
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
        await process.WaitForExitAsync(cancellationToken);
    }
}
