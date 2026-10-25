using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

/** PtySession 的窗格级订阅: 首帧是状态重建快照, 输出广播给每个订阅者, 订阅不独占会话。 */
public sealed class PtySessionSubscriptionTests
{
    [Fact(Timeout = 5000)]
    public async Task Subscribe_FirstFrame_IsScreenSnapshot()
    {
        using var host = new PtyHost();
        using var session = await host.StartAsync(StartInfo(OutputShell("SNAPMARK")), cancellationToken: TestContext.Current.CancellationToken);

        await ReadUntilAsync(session, "SNAPMARK", TimeSpan.FromSeconds(5));

        using var subscription = session.Subscribe();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Assert.True(await subscription.Output.WaitToReadAsync(timeout.Token));
        Assert.True(subscription.Output.TryRead(out var first));
        var snapshot = Encoding.UTF8.GetString(first);
        Assert.StartsWith("\u001b", snapshot);
        Assert.Contains("SNAPMARK", snapshot);
    }

    [Fact(Timeout = 5000)]
    public async Task Subscribe_BroadcastsToEverySubscriber()
    {
        using var host = new PtyHost();
        using var session = await host.StartAsync(StartInfo(InteractiveShell()), cancellationToken: TestContext.Current.CancellationToken);

        using var first = session.Subscribe();
        using var second = session.Subscribe();
        var (input, expected) = MarkerCommand();
        await session.WriteAsync(Encoding.UTF8.GetBytes(input), TestContext.Current.CancellationToken);

        Assert.Contains(expected, await ReadUntilAsync(first, expected, TimeSpan.FromSeconds(5)));
        Assert.Contains(expected, await ReadUntilAsync(second, expected, TimeSpan.FromSeconds(5)));
    }

    [Fact(Timeout = 5000)]
    public async Task Subscribe_CompletesAfterSessionExits()
    {
        using var host = new PtyHost();
        using var session = await host.StartAsync(
            StartInfo(InteractiveShell()), cancellationToken: TestContext.Current.CancellationToken);

        using var subscription = session.Subscribe();
        // 用订阅建立后的真实输入控制退出, 不用 ping/sleep 与 Windows 子进程启动时间赛跑。
        await session.WriteAsync(
            Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? "echo BYE & exit\r" : "echo BYE; exit\n"),
            TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var text = new StringBuilder();
        while (await subscription.Output.WaitToReadAsync(timeout.Token))
        {
            while (subscription.Output.TryRead(out var chunk))
                text.Append(Encoding.UTF8.GetString(chunk));
        }

        Assert.Contains("BYE", text.ToString());
    }

    [Fact(Timeout = 5000)]
    public async Task DisposeSubscription_StopsDelivery_WithoutStoppingSession()
    {
        using var host = new PtyHost();
        using var session = await host.StartAsync(StartInfo(InteractiveShell()), cancellationToken: TestContext.Current.CancellationToken);

        var subscription = session.Subscribe();
        subscription.Dispose();

        // 退订后会话仍然可写可读(订阅不拥有会话)。
        var (input, expected) = MarkerCommand();
        await session.WriteAsync(Encoding.UTF8.GetBytes(input), TestContext.Current.CancellationToken);
        Assert.Contains(expected, await ReadUntilAsync(session, expected, TimeSpan.FromSeconds(5)));
    }

    private static PtyStartInfo StartInfo((string FileName, string[] Arguments) command)
        => new()
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            Rows = 24,
            Columns = 80,
        };

    /** 先输出标记再驻留的脚本: 订阅要能看到"订阅之后仍在产生"的输出。 */
    private static (string FileName, string[] Arguments) OutputShell(string marker)
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", $"echo {marker} & ping -n 30 127.0.0.1 > nul"])
            : ("/bin/sh", ["-c", $"echo {marker}; sleep 30"]);

    private static (string FileName, string[] Arguments) InteractiveShell()
        => OperatingSystem.IsWindows()
            // /v:on: 延迟展开, 否则整行在解析时就展开了 %var%。
            ? ("cmd.exe", ["/v:on"])
            : ("/bin/sh", []);

    /** 一条"只有前台进程真的执行才会出现该输出"的命令(输入的约定标记不是最终输出)。 */
    private static (string Input, string Expected) MarkerCommand()
        => OperatingSystem.IsWindows()
            ? ("set /a m=6*7 & echo LIVE-!m!\r", "LIVE-42")
            : ("echo LIVE-$((6*7))\n", "LIVE-42");

    private static async Task<string> ReadUntilAsync(PtySession session, string marker, TimeSpan timeout)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeoutSource.CancelAfter(timeout);
        var text = new StringBuilder();
        var buffer = new byte[1024];
        try
        {
            while (!text.ToString().Contains(marker, StringComparison.Ordinal))
            {
                var read = await session.ReadAsync(buffer, timeoutSource.Token);
                if (read == 0)
                    break;
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }

        return text.ToString();
    }

    private static async Task<string> ReadUntilAsync(PtySessionSubscription subscription, string marker, TimeSpan timeout)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeoutSource.CancelAfter(timeout);
        var text = new StringBuilder();
        try
        {
            while (!text.ToString().Contains(marker, StringComparison.Ordinal))
            {
                if (!await subscription.Output.WaitToReadAsync(timeoutSource.Token))
                    break;
                while (subscription.Output.TryRead(out var chunk))
                    text.Append(Encoding.UTF8.GetString(chunk));
            }
        }
        catch (OperationCanceledException)
        {
        }

        return text.ToString();
    }
}
