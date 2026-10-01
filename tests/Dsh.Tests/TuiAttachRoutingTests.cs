using Dsh.Boot;

namespace Dsh.Tests;

/**
 * attach 分流: 未知 id 不能算 daemon PTY。
 * 注: 只做只读检查 —— 把本机 DSH_HOME 改到临时目录会污染并行跑的其它 PTY 测试, 所以不在这里改环境变量。
 */
public sealed class TuiAttachRoutingTests
{
    [Fact]
    public async Task IsDaemonPty_Is_False_For_Unknown_Ids()
    {
        Assert.False(await BootCli.IsDaemonPtyAsync("pty-deadbeefdeadbeefdeadbeefdeadbeef", TestContext.Current.CancellationToken));
        Assert.False(await BootCli.IsDaemonPtyAsync("session-not-a-pty", TestContext.Current.CancellationToken));
    }
}
