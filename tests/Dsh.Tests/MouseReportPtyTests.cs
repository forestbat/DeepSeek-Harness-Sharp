using System.Text;

namespace Dsh.Tests;

/**
 * 鼠标在真 TUI 会话里的四条硬要求(共用一次会话, 避免重复付 TUI 启动成本):
 *   1) 只有声明要鼠标的会话(常驻 TUI)才让代理打开宿主终端上报, 别的会话必须保持关闭;
 *   2) 代理打开上报后, 真灌进来的 X10 / SGR 报文**不得污染输入行**(ConPTY 不翻译 X10, 必须代理解析);
 *   3) 报文后继续打字必须正常(解析不能吞掉用户输入);
 *   4) 报文驱动的拖动必须真的作用于 TUI(右栏分隔线左移), 而不只是"没乱码"。
 */
[Collection(SerialProcessCollection.Name)]
public class MouseReportPtyTests
{
    [Fact]
    public async Task Reports_Never_Pollute_Input_And_Drag_Resizes_Divider()
    {
        using var harness = await PtyTuiHarness.StartAsync();
        if (harness is null)
            return;

        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.WaitForAsync("快捷键", TimeSpan.FromSeconds(150));

        // ① 夹具起的只是 proxy; 常驻 daemon 会话以本夹具 home 为家(见 PtyTuiHarness.StopResidentSessions)。
        //    只有它声明要鼠标; 普通命令会话必须为 false, 否则 Unix 上代理原样透传, 上报会被当成命令敲进那个程序。
        var sessions = await PtyDaemonClient.ListAsync(cancellationToken);
        var tui = Assert.Single(sessions, session => session.Command.Contains(harness.Home, StringComparison.OrdinalIgnoreCase));
        Assert.True(tui.WantsMouse);
        var shell = await PtyDaemonClient.StartAsync(
            new PtyDaemonStartParams
            {
                Id = $"test-shell-{Guid.NewGuid():N}",
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? ["/c", "exit"] : ["-c", "exit"],
                Rows = 24,
                Columns = 80,
            },
            cancellationToken);
        Assert.False(shell.WantsMouse);

        // ② 模拟宿主终端上报(拖动): 落点在输入行附近, 与真实污染一致; 两种编码都要被解析掉。
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
                await Task.Delay(15, cancellationToken);
            }
        }

        await Task.Delay(300, cancellationToken);

        // ③ 报文期间照常打字: 输入行只能有我们打进的字。
        await harness.WriteTextAsync("ok");
        await Task.Delay(250, cancellationToken);
        harness.SaveTranscript();

        var input = InputRow(harness.ReadScreen()).Trim();
        Assert.Contains("ok", input);
        Assert.Equal("> ok", input);

        // ③b 汉字必须原样送达: 终端发的是 UTF-8, 代理的控制台输入代码页不是 UTF-8 时会被降解成 GBK 字节,
        //     会话又按 UTF-8 解释 → 输入行与发出去的内容都是乱码。
        await ClearInputAsync(harness);
        await harness.WriteTextAsync("测试");
        await Task.Delay(250, cancellationToken);
        // 夹具的屏幕模型给全角字符留了一格占位, 去掉空格再比对。
        Assert.Equal(">测试", InputRow(harness.ReadScreen()).Trim().Replace(" ", string.Empty));

        // ④ 注入 X10 拖动右栏分隔线: 代理解析 → daemon 注入 MOUSE_EVENT → TUI 应重排右栏。
        var before = DividerColumn(harness.ReadScreen());
        if (before < 0)
            Assert.Skip("当前布局没有可拖动的竖直分隔线");

        const int row = 8;
        await SendX10(harness, 0, before, row);
        await Task.Delay(60, cancellationToken);
        await SendX10(harness, 32, before - 6, row);
        await Task.Delay(60, cancellationToken);
        await SendX10(harness, 3, before - 6, row);
        await Task.Delay(350, cancellationToken);

        var after = DividerColumn(harness.ReadScreen());
        Assert.True(after >= 0 && after <= before - 3, $"分隔线应从 {before} 左移至少 3 列, 实际 {after}");

        // ⑤ attach 之后改终端尺寸也必须被上报: 常驻 TUI 只在尺寸变化时重排。
        //    (旧实现只在 attach 那一刻上报一次, 之后调窗口没反应: 调大留白、调小被压。)
        // 缩到夹具视口(100x30)以内: 否则会话比屏幕大, 后面就看不到输入行了。
        harness.Session.Resize(rows: 24, columns: 80);
        Assert.True(await WaitForSessionSizeAsync(harness, columns: 80, rows: 24), "会话没有跟随客户端尺寸变化");

        // ⑥ /exit 之后代理必须结束, 并把终端还回去(离开备用屏 + 显示光标), 否则用户回不到宿主终端。
        //    先清掉第 ③b 步留在输入行里的"测试", 否则会变成把"测试/exit"当消息发出去。
        await ClearInputAsync(harness);
        await harness.WriteTextAsync("/exit");
        await harness.WriteBytesAsync(0x0D);
        var exited = false;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (harness.Session.Status != PtySessionStatus.Running)
            {
                exited = true;
                break;
            }

            await Task.Delay(100, cancellationToken);
        }

        Assert.True(exited, "会话退出后代理进程仍在运行");
        Assert.Contains("\u001b[?1049l", harness.Snapshot());
    }

    /** 退格清空输入行: 全角字符占位让"几个字符=几次退格"不可靠, 按屏幕状态收敛。 */
    private static async Task ClearInputAsync(PtyTuiHarness harness)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (InputRow(harness.ReadScreen()).Trim() == ">")
                return;
            await harness.WriteBytesAsync(0x7F);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Equal(">", InputRow(harness.ReadScreen()).Trim());
    }

    /** 轮询 daemon 里本夹具会话的尺寸, 直到等于期望值(尺寸上报是异步的)。 */
    private static async Task<bool> WaitForSessionSizeAsync(PtyTuiHarness harness, int columns, int rows)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var sessions = await PtyDaemonClient.ListAsync(TestContext.Current.CancellationToken);
            var session = sessions.FirstOrDefault(candidate =>
                candidate.Command.Contains(harness.Home, StringComparison.OrdinalIgnoreCase));
            if (session is not null && session.Columns == columns && session.Rows == rows)
                return true;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return false;
    }

    /** X10 报文: 三字节都是 `32 + 值`, 坐标是 0-based 时再 +1; b=0 左键按下, b=32 拖动, b=3 释放。 */
    private static Task SendX10(PtyTuiHarness harness, int buttons, int x, int y)
        => harness.WriteBytesAsync(Encoding.ASCII.GetBytes(
            $"\u001b[M{(char)(32 + buttons)}{(char)(33 + x)}{(char)(33 + y)}"));

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
