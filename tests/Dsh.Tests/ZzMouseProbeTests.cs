using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

/**
 * 临时探针(不是产品测试): 用非产品子进程在真 ConPTY 里按记录读, 观察 conhost 把鼠标报文
 * 翻成 MOUSE_EVENT 还是逐字符 KEY_EVENT, 以及 VTI 开/关时的差异。用完删除。
 */
[Collection(SerialProcessCollection.Name)]
public class ZzMouseProbeTests
{
    [Theory]
    [InlineData("clear")]
    [InlineData("vti")]
    public async Task Probe_Mouse_Report_Conversion(string mode)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var script = Path.Combine(root, "artifacts", "debug-screenshots", "zz-mouse-probe.py");
        var report = Path.Combine(root, "artifacts", "debug-screenshots", $"zz-mouse-probe-{mode}.txt");
        if (!File.Exists(script))
            Assert.Skip("探针脚本缺失");

        using var host = new PtyHost();
        var session = await host.StartAsync(
            new PtyStartInfo
            {
                FileName = "python",
                Arguments = ["-u", script, mode],
                Rows = 30,
                Columns = 120,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitForAsync(session, "READY mode=", TimeSpan.FromSeconds(60));
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var log = new StringBuilder();
        log.AppendLine($"# READY dump:\n{Tail(session, 0)}");

        await session.WriteAsync(Encoding.ASCII.GetBytes("\u001b[M@4;"), TestContext.Current.CancellationToken);
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        await session.WriteAsync(Encoding.ASCII.GetBytes("\u001b[<32;20;27M"), TestContext.Current.CancellationToken);
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        await session.WriteAsync(Encoding.ASCII.GetBytes("\u001b[?1000h\u001b[?1002h\u001b[?1006h"), TestContext.Current.CancellationToken);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        await session.WriteAsync(Encoding.ASCII.GetBytes("\u001b[M@5;"), TestContext.Current.CancellationToken);
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        log.AppendLine($"# FULL STREAM:\n{Text(session)}");
        await File.WriteAllTextAsync(report, log.ToString(), TestContext.Current.CancellationToken);
        await session.StopAsync();
    }

    private static string Text(PtySession session)
        => Encoding.UTF8.GetString(session.Snapshot()).Replace("\u001b", "\\e");

    private static string Tail(PtySession session, int seen)
    {
        var text = Text(session);
        return text.Length > seen ? text[seen..] : "(no new bytes)";
    }

    private static async Task WaitForAsync(PtySession session, string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Text(session).Contains(marker, StringComparison.Ordinal))
                return;
            await Task.Delay(80, TestContext.Current.CancellationToken);
        }
    }
}
