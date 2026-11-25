using System.Text;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * Shift+Enter 换行的前提: 终端必须收到"请编码带修饰键的按键"的请求(kitty 键盘协议 / xterm modifyOtherKeys),
 * 否则 Shift+Enter 与 Enter 是同一个字节(\r), 分不出来。这里钉住两件事:
 *   1) 我们发出的开关序列就是那两种协议;
 *   2) 两种协议下 Shift+Enter 的编码都能解成"带 Shift 的 Enter"(而不是被丢弃, 也不是裸回车)。
 * 解码之后到底插入换行还是发送, 见 ChatWindowTranscriptTests 里的草稿用例。
 */
public class TerminalModifiedEnterTests
{
    [Fact]
    public void Modes_Request_Both_Known_Keyboard_Protocols_And_Reset_Them()
    {
        Assert.Contains("\u001b[>1u", TerminalInputModes.Enable, StringComparison.Ordinal);
        Assert.Contains("\u001b[>4;2m", TerminalInputModes.Enable, StringComparison.Ordinal);
        // 退出必须按相反顺序复位, 否则宿主终端会留在改过的键盘模式里。
        Assert.Contains("\u001b[<u", TerminalInputModes.Disable, StringComparison.Ordinal);
        Assert.Contains("\u001b[>4;0m", TerminalInputModes.Disable, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\u001b[13;2u")]
    [InlineData("\u001b[27;2;13~")]
    public void Shift_Enter_Decodes_To_Enter_With_Shift(string sequence)
    {
        var parser = new TerminalInputParser();
        parser.Append(Encoding.ASCII.GetBytes(sequence));

        Assert.True(parser.TryParse(out var input));
        Assert.False(input.IsPaste);
        Assert.Equal(ConsoleKey.Enter, input.Key.Key);
        Assert.True((input.Key.Modifiers & ConsoleModifiers.Shift) != 0);
    }
}
