using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

/**
 * attach 的状态重建: daemon 侧重建 VT 主/备用屏、输入模式、画笔与光标,
 * 而不是把历史字节再放一遍 —— 全屏程序(vim/htop)退出与 tmux 切换后也必须保持一致。
 */
public sealed class PtySnapshotTests
{
    [Fact(Timeout = 5000)]
    public async Task Snapshot_Starts_With_Full_Redraw_Preamble()
    {
        using var host = new PtyHost();
        var shell = PtyShell.Resolve();
        var session = await host.StartAsync(
            new PtyStartInfo { FileName = shell, Arguments = PtyShell.Arguments(shell), Rows = 6, Columns = 40 },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var snapshot = Encoding.UTF8.GetString(session.Snapshot());

            // 取消可能被旧连接截断的控制序列, 复位后逐行重画; 不强制进入备用屏。
            Assert.StartsWith("\u0018\u001bc\u001b[0m\u001b[2J\u001b[H", snapshot, StringComparison.Ordinal);
            Assert.Contains("\u001b[1;1H", snapshot, StringComparison.Ordinal);
            Assert.Contains("\u001b[6;1H", snapshot, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b[K", snapshot, StringComparison.Ordinal);
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact(Timeout = 5000)]
    public async Task Snapshot_Renders_The_Emulated_Screen_Not_The_History()
    {
        if (OperatingSystem.IsWindows())
            return;   // 用 /bin/sh 布置画面, 与另外两个 daemon PTY 用例一致

        using var host = new PtyHost();
        var session = await host.StartAsync(
            new PtyStartInfo
            {
                // 画在固定位置后不退出: 模拟"全屏程序还在跑", attach 时不能靠新字节, 只能靠状态重建。
                FileName = "/bin/sh",
                Arguments = ["-c", "printf '\\033[2J\\033[3;7Hdsh-screen-marker'; sleep 30"],
                Rows = 8,
                Columns = 40,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            string snapshot = "";
            while (DateTime.UtcNow < deadline)
            {
                snapshot = Encoding.UTF8.GetString(session.Snapshot());
                if (snapshot.Contains("dsh-screen-marker", StringComparison.Ordinal))
                    break;
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            Assert.Contains("dsh-screen-marker", snapshot, StringComparison.Ordinal);
            // 标记落在第 3 行: 重建必须按行定位(原始字节回放不会生成这种逐行定位前缀)。
            var row3 = snapshot.IndexOf("\u001b[3;1H", StringComparison.Ordinal);
            Assert.True(row3 >= 0, "第 3 行应有定位前缀\n" + snapshot.Replace("\u001b", "\\e", StringComparison.Ordinal));
            var marker = snapshot.IndexOf("dsh-screen-marker", StringComparison.Ordinal);
            Assert.True(marker > row3, "标记应出现在第 3 行的定位之后");
        }
        finally
        {
            await session.StopAsync();
        }
    }
}
