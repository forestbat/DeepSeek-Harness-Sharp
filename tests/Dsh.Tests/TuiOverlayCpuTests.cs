using System.Text;

namespace Dsh.Tests;

/**
 * CPU(终端 ANSI)路径的真实 TUI 验证: 打开 / 命令浮层、并在输入区输入中文时,
 * 浮层 / 正文 / 右侧栏三者不得互相混排——只属于右栏的内容不得出现在正文列, 右栏也不得被挤掉。
 * 与 GPU 路径的取证帧(artifacts/debug-screenshots/r*.png、g*.grid.txt)互补。
 */
[Collection(SerialProcessCollection.Name)]
public class TuiOverlayCpuTests
{
    [Fact]
    public async Task Slash_Menu_Does_Not_Mix_Right_Panel_Into_Main()
    {
        using var harness = await PtyTuiHarness.StartAsync();
        if (harness is null)
            return;

        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.WaitForAsync("快捷键", TimeSpan.FromSeconds(150));

        var divider = DividerColumn(harness.ReadScreen());
        Assert.True(divider > 0, "初始布局里找不到右侧栏分隔线");

        // 先打 / 打开浮层, 再输入中文(宽字符): 覆盖宽字符是历史上"行右移/串栏"的触发点。
        await harness.WriteTextAsync("/");
        await Task.Delay(300, cancellationToken);
        await harness.WriteTextAsync("项目记忆与快捷键测试");
        await Task.Delay(600, cancellationToken);
        harness.SaveTranscript();

        var screen = harness.ReadScreen();
        divider = DividerColumn(screen);
        Assert.True(divider > 0, "开浮层后找不到右侧栏分隔线");

        var main = Columns(screen, 0, divider);
        var panel = Columns(screen, divider + 1, screen.GetLength(0));

        // 屏幕模型给全角字符留了一格占位, 去空格后再比标记。
        // 右栏专属内容不得出现在正文列。
        Assert.DoesNotContain("上下文", main.Replace(" ", string.Empty));
        // 右栏本身不得被浮层清掉。
        Assert.Contains("上下文", panel.Replace(" ", string.Empty));
        // 浮层确实画在正文区(否则上面两条是空转)。
        Assert.Contains("Commands", main);
    }

    private static string Columns(char[,] screen, int from, int to)
    {
        var builder = new StringBuilder();
        for (var y = 0; y < screen.GetLength(1); y++)
        {
            for (var x = from; x < to; x++)
                builder.Append(screen[x, y]);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /** 屏幕上竖直分隔线所在列(竖线最多的中间列)。 */
    private static int DividerColumn(char[,] screen)
    {
        var best = -1;
        var bestCount = 0;
        for (var x = 1; x < screen.GetLength(0) - 1; x++)
        {
            var count = 0;
            for (var y = 0; y < screen.GetLength(1); y++)
            {
                if (screen[x, y] is '│' or '┃' or '║' or '|')
                    count++;
            }

            if (count > bestCount)
            {
                bestCount = count;
                best = x;
            }
        }

        return bestCount >= 5 ? best : -1;
    }
}
