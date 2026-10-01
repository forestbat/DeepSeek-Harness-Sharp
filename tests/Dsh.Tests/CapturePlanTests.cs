using Dsh.Tui;

namespace Dsh.Tests;

/** 方案 B 的脚本解析与执行游标(纯逻辑, 不涉及 GL/窗口)。 */
public sealed class CapturePlanTests
{
    [Fact]
    public void Parse_Reads_Directives_And_Escapes()
    {
        var steps = CapturePlan.Parse("""
            # Ctrl+P 浮层
            keys \x10
            wait 200
            keys /model\r
            capture 01-model
            """);

        Assert.Equal(4, steps.Count);
        Assert.Equal(CapturePlanAction.Keys, steps[0].Action);
        Assert.Equal("\u0010", steps[0].Value);
        Assert.Equal(CapturePlanAction.Wait, steps[1].Action);
        Assert.Equal(200, steps[1].DelayMs);
        Assert.Equal("/model\r", steps[2].Value);
        Assert.Equal(CapturePlanAction.Capture, steps[3].Action);
        Assert.Equal("01-model", steps[3].Value);
        Assert.Equal(CapturePlan.CaptureSettleMs, steps[3].DelayMs);
    }

    [Theory]
    [InlineData("keys \\q")]
    [InlineData("capture")]
    [InlineData("dance 1")]
    [InlineData("wait abc")]
    public void Parse_Rejects_Invalid_Lines(string line)
    {
        var error = Assert.Throws<FormatException>(() => CapturePlan.Parse(line));

        Assert.Contains("第 1 行", error.Message);
    }

    [Fact]
    public void Runner_Honours_Delays_And_Finishes()
    {
        var runner = new CapturePlanRunner(
        [
            new CapturePlanStep(CapturePlanAction.Keys, "a", 0),
            new CapturePlanStep(CapturePlanAction.Keys, "b", 500),
        ]);

        Assert.Equal("a", runner.Next(1_000)!.Value);
        Assert.Null(runner.Next(1_100));
        Assert.Equal("b", runner.Next(1_500)!.Value);
        Assert.True(runner.IsFinished);
        Assert.Null(runner.Next(9_999));
    }

    /** 脚本字符 → 控制台按键: 控制键要带上 Ctrl 语义, 普通字符按文本输入。 */
    [Fact]
    public void CaptureKeys_Map_Control_Characters()
    {
        Assert.Equal(ConsoleKey.Enter, CaptureKeys.ToKeyInfo('\r').Key);
        Assert.Equal(ConsoleKey.Tab, CaptureKeys.ToKeyInfo('\t').Key);
        Assert.Equal(ConsoleKey.Escape, CaptureKeys.ToKeyInfo('\e').Key);
        Assert.Equal(ConsoleKey.Backspace, CaptureKeys.ToKeyInfo('\b').Key);
        Assert.Equal(ConsoleKey.C, CaptureKeys.ToKeyInfo('\u0003').Key);
        Assert.Equal(ConsoleKey.X, CaptureKeys.ToKeyInfo('\u0018').Key);
        Assert.Equal(ConsoleKey.P, CaptureKeys.ToKeyInfo('\u0010').Key);
        Assert.True((CaptureKeys.ToKeyInfo('\u0018').Modifiers & ConsoleModifiers.Control) != 0);
        Assert.Equal(ConsoleKey.NoName, CaptureKeys.ToKeyInfo('a').Key);
    }
}
