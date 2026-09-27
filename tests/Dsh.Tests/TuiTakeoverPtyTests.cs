namespace Dsh.Tests;

/**
 * 真实 dsh TUI 跑在工程自身 PTY 上的接管验证(文本级):
 * 备用屏幕/光标隐藏序列、CJK UI、明文输入到达、控制键、双 Ctrl+C 退出后恢复序列。
 */
[Collection(SerialProcessCollection.Name)] // 真进程: 与其它真进程用例串行, 避免并行负载抖动
public class TuiTakeoverPtyTests
{
    [Fact]
    public async Task Real_Tui_Takes_Over_Alternate_Screen_And_Restores_On_Exit()
    {
        using var harness = await PtyTuiHarness.StartAsync();
        if (harness is null)
            return;

        var startup = await harness.WaitForAsync("\u001b[?1049h", TimeSpan.FromSeconds(60));
        harness.SaveTranscript();
        Assert.Contains("\u001b[?1049h", startup);
        Assert.Contains("\u001b[?25l", startup);

        var rendered = await harness.WaitForAsync("快捷键", TimeSpan.FromSeconds(60));
        harness.SaveTranscript();
        Assert.Contains("快捷键", rendered);
        Assert.Contains("上下文", rendered);

        await harness.WriteTextAsync("zz");
        var typed = await harness.WaitForAsync("zz", TimeSpan.FromSeconds(30));
        harness.SaveTranscript();
        Assert.Contains("zz", typed);

        await harness.WriteBytesAsync(0x18);
        var control = await harness.WaitForAsync("Ctrl+X:", TimeSpan.FromSeconds(30));
        harness.SaveTranscript();
        Assert.Contains("Ctrl+X:", control);

        // Ctrl+X 已置前缀态: 先单独发 Esc 收尾, 再连按两次 Ctrl+C。
        // 三段必须分开写: ESC 与 0x03 在同一块会构成 Alt+Ctrl+C, 少算一次退出确认。
        await harness.WriteBytesAsync(0x1b);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await harness.WriteBytesAsync(0x03);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await harness.WriteBytesAsync(0x03);
        var exit = await harness.WaitForAsync("\u001b[?1049l", TimeSpan.FromSeconds(60));
        harness.SaveTranscript();
        Assert.Contains("\u001b[?1049l", exit);
        Assert.Contains("\u001b[?25h", exit);
    }
}

