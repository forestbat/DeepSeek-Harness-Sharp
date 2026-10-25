using System.Diagnostics;
using Dsh.Tui;

namespace Dsh.Tests;

/** Unix 输入源的唤醒管道: Wake 之后 Read 必须及时返回, 不能卡在排空管道上。 */
public class TerminalInputReaderTests
{
    private const int ReadTimeoutMilliseconds = 5000;
    private static readonly TimeSpan HangDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public void WakePipe_IsNonBlocking()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("唤醒管道只存在于 Unix 输入源");

        using var reader = new TerminalInputReader();

        Assert.True(reader.WakePipeIsNonBlocking);
    }

    [Fact]
    public void Wake_ReturnsBlockedRead()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("唤醒管道只存在于 Unix 输入源");

        using var reader = new TerminalInputReader();
        var elapsed = TimeSpan.MaxValue;
        // 卡死时线程停在原生 read 里, 测试超时打断不了它; 用后台线程 + Join 让卡死变成失败而不是挂住整个测试进程。
        var worker = new Thread(() =>
        {
            var watch = Stopwatch.StartNew();
            reader.Read(ReadTimeoutMilliseconds);
            elapsed = watch.Elapsed;
        }) { IsBackground = true };

        reader.Wake();
        worker.Start();

        Assert.True(worker.Join(HangDeadline), "Read did not return after Wake");
        Assert.True(elapsed < HangDeadline, $"Read returned after {elapsed}");
    }
}
