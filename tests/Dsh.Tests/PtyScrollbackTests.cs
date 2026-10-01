using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

/** 常驻泵: 没有读者时也要读走 PTY 输出(否则子进程写满缓冲会卡住), 并保留最近内容供 attach 回放。 */
public sealed class PtyScrollbackTests
{
    [Fact]
    public async Task Session_Keeps_Scrollback_Without_Readers()
    {
        using var host = new PtyHost();
        var shell = PtyShell.Resolve();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = shell,
            Arguments = PtyShell.Arguments(shell),
            Rows = 24,
            Columns = 80,
        }, cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // 全程不调用 ReadAsync: 泵必须自己把输出读走并留在回放缓冲里。
            await session.WriteAsync(Encoding.UTF8.GetBytes("echo dsh-scrollback-marker\r"), TestContext.Current.CancellationToken);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline
                && !Encoding.UTF8.GetString(session.Scrollback()).Contains("dsh-scrollback-marker", StringComparison.Ordinal))
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            Assert.Contains("dsh-scrollback-marker", Encoding.UTF8.GetString(session.Scrollback()), StringComparison.Ordinal);
        }
        finally
        {
            await session.StopAsync();
        }
    }
}
