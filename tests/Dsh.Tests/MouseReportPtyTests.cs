using System.Text;

namespace Dsh.Tests;

/**
 * 鼠标上报的三条硬要求(同一次真 TUI 会话内一次跑完, 避免重复付启动成本):
 *   1) 宿主终端**永远不被打开鼠标上报**: 常驻 TUI 不该请求鼠标(?1000h/?1002h/?1006h), 代理也只放行关闭序列;
 *   2) 终端形态下真灌进来的 X10 / SGR 报文**不得污染输入行**;
 *   3) 报文后继续打字必须正常(过滤不能吞掉用户输入)。
 */
[Collection(SerialProcessCollection.Name)]
public class MouseReportPtyTests
{
    [Fact]
    public async Task Host_Terminal_Never_Reports_Mouse_And_Reports_Never_Pollute_Input()
    {
        using var harness = await PtyTuiHarness.StartAsync();
        if (harness is null)
            return;

        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.WaitForAsync("快捷键", TimeSpan.FromSeconds(60));

        // ① 常驻 TUI 不能请求鼠标; 代理 attach 时必须把终端复位到"不开鼠标"(上一次运行可能留下开启状态)
        var startup = harness.Snapshot();
        Assert.DoesNotContain("\u001b[?1000h", startup);
        Assert.DoesNotContain("\u001b[?1002h", startup);
        Assert.DoesNotContain("\u001b[?1006h", startup);
        Assert.Contains("\u001b[?1006l", startup);

        // ② 模拟宿主终端上报(拖动): 落点在输入行附近, 与真实污染一致
        foreach (var encoding in new[] { "x10", "sgr" })
        {
            for (var step = 0; step < 8; step++)
            {
                var x = 20 + step;
                var y = 27;
                var report = encoding == "x10"
                    ? $"\u001b[M{(char)(32 + 32)}{(char)(32 + x)}{(char)(32 + y)}"
                    : $"\u001b[<32;{x};{y}M";
                await harness.WriteBytesAsync(Encoding.ASCII.GetBytes(report));
                await Task.Delay(30, cancellationToken);
            }
        }

        await Task.Delay(800, cancellationToken);

        // ③ 报文期间照常打字: 输入行只能有我们打进的字
        await harness.WriteTextAsync("ok");
        await Task.Delay(600, cancellationToken);
        harness.SaveTranscript();

        var input = InputRow(harness.ReadScreen()).Trim();
        Assert.Contains("ok", input);
        Assert.Equal("> ok", input);
    }

    /** 输入行: 该行以 '>' 开头(单窗格布局下输入行固定在状态栏上方)。 */
    private static string InputRow(char[,] screen)
    {
        for (var y = 0; y < screen.GetLength(1); y++)
        {
            var line = new StringBuilder();
            for (var x = 0; x < screen.GetLength(0); x++)
                line.Append(screen[x, y]);
            var text = line.ToString().TrimEnd();
            if (text.TrimStart().StartsWith('>'))
                return text;
        }

        return "";
    }
}
