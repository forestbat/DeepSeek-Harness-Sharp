using System.Text;
using Dsh.Pty;
using Xunit;

namespace Dsh.Tests;

/**
 * Unix 交互 shell 的控制终端语义: 子进程必须是新会话首进程且把 pty 从端设为控制终端, 否则 bash 报
 * "no job control in this shell"、readline 不工作——shell 窗格与 agent 终端工具都依赖这一点。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class PtyInteractiveShellTests
{
    [Fact]
    public async Task Interactive_Shell_Reports_Job_Control_And_Executes_Command()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("setsid/控制终端语义只在 Unix pty 后端有意义");

        var shell = PtyShell.Resolve();
        var output = new StringBuilder();
        var pumpCancellation = new CancellationTokenSource();
        var host = new PtyHost();
        try
        {
            var session = await host.StartAsync(new PtyStartInfo
            {
                FileName = shell,
                Arguments = PtyShell.Arguments(shell),
                Rows = 24,
                Columns = 80,
                Environment = PtyShell.ChildEnvironment(),
            }, cancellationToken: TestContext.Current.CancellationToken);

            var pump = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                while (!pumpCancellation.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = await session.ReadAsync(buffer, pumpCancellation.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    if (read <= 0)
                    {
                        if (session.Status != PtySessionStatus.Running)
                            return;
                        await Task.Delay(20, TestContext.Current.CancellationToken);
                        continue;
                    }
                    lock (output)
                        output.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            }, TestContext.Current.CancellationToken);

            await session.WriteAsync("echo probe-$(echo ok)\n"u8.ToArray(), TestContext.Current.CancellationToken);
            var text = await WaitForAsync(output, "probe-ok", TimeSpan.FromSeconds(20));
            Assert.Contains("probe-ok", text);
            Assert.DoesNotContain("no job control", text);

            pumpCancellation.Cancel();
            _ = pump;
            await host.StopAsync(session.Id.Value);
        }
        finally
        {
            pumpCancellation.Dispose();
            host.Dispose();
        }
    }

    private static async Task<string> WaitForAsync(StringBuilder output, string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            string text;
            lock (output)
                text = output.ToString();
            if (text.Contains(marker, StringComparison.Ordinal) || DateTime.UtcNow >= deadline)
                return text;
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }
}
