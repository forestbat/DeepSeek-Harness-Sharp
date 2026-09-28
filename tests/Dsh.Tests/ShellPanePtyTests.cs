using Xunit;

namespace Dsh.Tests;

/**
 * shell 窗格真 PTY 端到端: `dsh tui --shell` 启动即带 shell 窗格, 键盘直达 PTY, shell 真执行命令。
 * 命令用 `echo dsh-$(echo pty)` 这类"输出串不出现在输入里"的写法, 避免把输入回显当成执行结果。
 */
[Collection(SerialProcessCollection.Name)] // 真进程: 与其它真进程用例串行, 避免并行负载抖动
public sealed class ShellPanePtyTests
{
    [Fact]
    public async Task ShellPane_Executes_Command_And_Closes_By_Subcommand()
    {
        var shell = Dsh.Pty.PtyShell.Resolve();
        if (Path.GetFileNameWithoutExtension(shell).Equals("cmd", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("cmd.exe 不支持 $(...) 子表达式, 换 pwsh/bash 后本条测试才有意义");

        using var harness = await PtyTuiHarness.StartAsync("--shell");
        if (harness is null)
            return;

        var started = await harness.WaitForAsync("shell · ", TimeSpan.FromSeconds(60));
        harness.SaveTranscript();
        Assert.Contains("shell · ", started);

        // 分块输入(每块 3 字符 + 间隔): 模拟真人击键。整串一次性灌入的路径由 ShellPane_Accepts_Single_Burst_Input 覆盖。
        foreach (var chunk in Chunk("echo dsh-$(echo pty)", 3))
        {
            await harness.WriteTextAsync(chunk);
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await harness.WriteBytesAsync(0x0d);
        var executed = await WaitForScreenAsync(harness, "dsh-pty", TimeSpan.FromSeconds(30));
        harness.SaveTranscript();
        Assert.True(executed.Contains("dsh-pty", StringComparison.Ordinal), $"屏幕上未见到命令输出:\n{executed}");

        // Ctrl+X - 关闭 shell 窗格(焦点在 shell 时前缀仍归窗口)
        await harness.WriteBytesAsync(0x18);
        var hint = await harness.WaitForAsync("Ctrl+X:", TimeSpan.FromSeconds(10));
        Assert.Contains("Ctrl+X:", hint);
        await harness.WriteTextAsync("-");
        var closed = await WaitForScreenMissingAsync(harness, "shell · ", TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("shell · ", closed);
    }

    /**
     * 回归: 宿主把整串(20 字符 + 回车)一次性写入时, 界面曾不显示命令输出(根因: TuiRunner 渲染帧内
     * DrainInputUntil 消费了唤醒字节却未执行对应 UI 动作, 随后直接阻塞在 Read, 输出泵的 Invalidate
     * 被 _wakePending 挡死——并非输入丢字符)。与分块用例同路径, 只是输入为单次写入。
     */
    [Fact]
    public async Task ShellPane_Accepts_Single_Burst_Input()
    {
        var shell = Dsh.Pty.PtyShell.Resolve();
        if (Path.GetFileNameWithoutExtension(shell).Equals("cmd", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("cmd.exe 不支持 $(...) 子表达式, 换 pwsh/bash 后本条测试才有意义");

        using var harness = await PtyTuiHarness.StartAsync("--shell");
        if (harness is null)
            return;

        var started = await harness.WaitForAsync("shell · ", TimeSpan.FromSeconds(60));
        Assert.Contains("shell · ", started);

        const string command = "echo dsh-$(echo pty)";
        await harness.WriteTextAsync(command);
        await harness.WriteBytesAsync(0x0d);
        var executed = await WaitForScreenAsync(harness, "dsh-pty", TimeSpan.FromSeconds(30));
        harness.SaveTranscript();
        Assert.True(executed.Contains("dsh-pty", StringComparison.Ordinal), $"整串灌入后屏幕上未见到命令输出:\n{executed}");
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var index = 0; index < text.Length; index += size)
            yield return text.Substring(index, Math.Min(size, text.Length - index));
    }

    private static async Task<string> WaitForScreenAsync(PtyTuiHarness harness, string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var text = ScreenText(harness);
        while (DateTime.UtcNow < deadline && !text.Contains(marker, StringComparison.Ordinal))
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            text = ScreenText(harness);
        }
        return text;
    }

    private static async Task<string> WaitForScreenMissingAsync(PtyTuiHarness harness, string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var text = ScreenText(harness);
        while (DateTime.UtcNow < deadline && text.Contains(marker, StringComparison.Ordinal))
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            text = ScreenText(harness);
        }
        return text;
    }

    /** 当前一屏的纯文本(不看历史输出流): 断言"现在的屏幕长什么样"。 */
    private static string ScreenText(PtyTuiHarness harness)
    {
        var screen = harness.ReadScreen();
        var text = new System.Text.StringBuilder();
        for (var y = 0; y < PtyTuiHarness.Rows; y++)
        {
            for (var x = 0; x < PtyTuiHarness.Columns; x++)
                text.Append(screen[x, y] == '\0' ? ' ' : screen[x, y]);
            text.Append('\n');
        }
        return text.ToString();
    }
}


