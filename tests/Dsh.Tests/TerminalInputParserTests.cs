using System.Text;
using Dsh.Tui;

namespace Dsh.Tests;

/** 原始字节解码: 键盘转义序列与 SGR 鼠标。 */
public class TerminalInputParserTests
{
    [Fact]
    public void Printable_Ascii_Becomes_A_Key()
    {
        var parser = new TerminalInputParser();
        parser.Append("a"u8);

        Assert.True(parser.TryParse(out var input));
        Assert.False(input.IsMouse);
        Assert.Equal('a', input.Key.KeyChar);
    }

    [Fact]
    public void Cjk_Character_Decodes_As_Single_Key()
    {
        var parser = new TerminalInputParser();
        parser.Append("汉"u8);
        Assert.True(parser.TryParse(out var input));
        Assert.Equal('汉', input.Key.KeyChar);
    }

    [Theory]
    [InlineData(0x0d, ConsoleKey.Enter)]
    [InlineData(0x09, ConsoleKey.Tab)]
    [InlineData(0x08, ConsoleKey.Backspace)]
    [InlineData(0x7f, ConsoleKey.Backspace)]
    public void Control_Bytes_Map_To_Named_Keys(byte value, ConsoleKey expected)
    {
        var parser = new TerminalInputParser();
        parser.Append([value]);

        Assert.True(parser.TryParse(out var input));
        Assert.Equal(expected, input.Key.Key);
    }

    [Fact]
    public void Ctrl_X_Is_Control_Modified_Letter()
    {
        var parser = new TerminalInputParser();
        parser.Append([0x18]);

        Assert.True(parser.TryParse(out var input));
        Assert.Equal(ConsoleKey.X, input.Key.Key);
        Assert.True((input.Key.Modifiers & ConsoleModifiers.Control) != 0);
    }

    [Theory]
    [InlineData("\u001b[A", ConsoleKey.UpArrow)]
    [InlineData("\u001b[B", ConsoleKey.DownArrow)]
    [InlineData("\u001b[C", ConsoleKey.RightArrow)]
    [InlineData("\u001b[D", ConsoleKey.LeftArrow)]
    [InlineData("\u001b[5~", ConsoleKey.PageUp)]
    [InlineData("\u001b[6~", ConsoleKey.PageDown)]
    [InlineData("\u001b[3~", ConsoleKey.Delete)]
    [InlineData("\u001b[H", ConsoleKey.Home)]
    [InlineData("\u001b[F", ConsoleKey.End)]
    [InlineData("\u001bOA", ConsoleKey.UpArrow)]
    public void Escape_Sequences_Map_To_Named_Keys(string sequence, ConsoleKey expected)
    {
        var parser = new TerminalInputParser();
        parser.Append(Encoding.ASCII.GetBytes(sequence));

        Assert.True(parser.TryParse(out var input));
        Assert.Equal(expected, input.Key.Key);
    }

    /**
     * 孤立 ESC 必须先挂起: 立刻当 Esc 键上报的话, 被读边界切开的鼠标报文(ESC 单独一段)会把剩下的
     * `[M`+3 / `[<…M` 字节漏成键入文本。静默期由调用方用 TryFlushPendingEscape 冲刷成真正的 Esc 键。
     */
    [Fact]
    public void Lone_Escape_Is_Held_Then_Flushed_As_Escape_Key()
    {
        var parser = new TerminalInputParser();
        parser.Append([0x1b]);

        Assert.True(parser.HasPendingEscape);
        Assert.False(parser.TryParse(out _));
        Assert.True(parser.TryFlushPendingEscape(out var input));
        Assert.Equal(ConsoleKey.Escape, input.Key.Key);
    }

    [Fact]
    public void Sequence_Split_Across_Appends_Is_Buffered()
    {
        var parser = new TerminalInputParser();
        parser.Append([0x1b, (byte)'[']);

        Assert.False(parser.TryParse(out _));
        parser.Append([(byte)'A']);

        Assert.True(parser.TryParse(out var input));
        Assert.Equal(ConsoleKey.UpArrow, input.Key.Key);
    }

    [Fact]
    public void Sgr_Mouse_Press_Is_Parsed_With_Zero_Based_Coordinates()
    {
        var parser = new TerminalInputParser();
        parser.Append("\u001b[<0;12;5M"u8);

        Assert.True(parser.TryParse(out var input));
        Assert.True(input.IsMouse);
        var mouse = input.Mouse!.Value;
        Assert.Equal(0, mouse.Button);
        Assert.True(mouse.Pressed);
        Assert.Equal(11, mouse.X);
        Assert.Equal(4, mouse.Y);
        Assert.False(mouse.IsWheel);
    }

    [Fact]
    public void Sgr_Mouse_Release_Is_Parsed()
    {
        var parser = new TerminalInputParser();
        parser.Append("\u001b[<0;3;3m"u8);

        Assert.True(parser.TryParse(out var input));
        Assert.False(input.Mouse!.Value.Pressed);
    }

    [Theory]
    [InlineData("\u001b[<64;3;2M", 1)]
    [InlineData("\u001b[<65;3;2M", -1)]
    public void Sgr_Wheel_Reports_Delta(string sequence, int expectedDelta)
    {
        var parser = new TerminalInputParser();
        parser.Append(Encoding.ASCII.GetBytes(sequence));

        Assert.True(parser.TryParse(out var input));
        var mouse = input.Mouse!.Value;
        Assert.True(mouse.IsWheel);
        Assert.Equal(expectedDelta, mouse.WheelDelta);
    }

    [Fact]
    public void Mixed_Stream_Yields_Events_In_Order()
    {
        var parser = new TerminalInputParser();
        parser.Append("\u001b[<0;2;2M"u8);
        parser.Append("x"u8);
        parser.Append("\u001b[B"u8);

        Assert.True(parser.TryParse(out var first));
        Assert.True(first.IsMouse);
        Assert.True(parser.TryParse(out var second));
        Assert.Equal('x', second.Key.KeyChar);
        Assert.True(parser.TryParse(out var third));
        Assert.Equal(ConsoleKey.DownArrow, third.Key.Key);
        Assert.False(parser.TryParse(out _));
    }

    [Fact]
    public void Mouse_Enable_And_Disable_Sequences_Are_Sgr()
    {
        Assert.Contains("1006", TerminalRawMode.MouseEnableSequenceForTests);
        Assert.Contains("1006", TerminalRawMode.MouseDisableSequenceForTests);
        Assert.Contains("h", TerminalRawMode.MouseEnableSequenceForTests);
        Assert.Contains("l", TerminalRawMode.MouseDisableSequenceForTests);
        // 退出时必须把 1003(全量移动)/1015(urxvt) 也复位, 否则终端仍持续刷鼠标上报。
        Assert.Contains("1003", TerminalRawMode.MouseDisableSequenceForTests);
        Assert.Contains("1015", TerminalRawMode.MouseDisableSequenceForTests);
    }

    [Fact]
    public void X10_Mouse_Press_Is_Consumed_Not_Leaked_As_Text()
    {
        var parser = new TerminalInputParser();
        // X10: ESC [ M Cb Cx Cy; 左键点 (36,13)(1 基) → Cb=32+0, Cx=32+36='D', Cy=32+13='-'
        parser.Append("\u001b[M D-"u8);

        Assert.True(parser.TryParse(out var input));
        Assert.True(input.IsMouse);
        var mouse = input.Mouse!.Value;
        Assert.Equal(0, mouse.Button);
        Assert.True(mouse.Pressed);
        Assert.Equal(35, mouse.X);
        Assert.Equal(12, mouse.Y);
        Assert.False(parser.TryParse(out _));
    }

    [Fact]
    public void X10_Mouse_Split_Payload_Is_Buffered()
    {
        var parser = new TerminalInputParser();
        parser.Append("\u001b[M "u8);

        Assert.False(parser.TryParse(out _));
        parser.Append("D-"u8);

        Assert.True(parser.TryParse(out var input));
        Assert.True(input.IsMouse);
        Assert.Equal(35, input.Mouse!.Value.X);
    }

    [Fact]
    public void Sgr_Drag_Reports_Move_With_Held_Button()
    {
        var parser = new TerminalInputParser();
        // SGR: CSI < b;x;y M, b=32 表示带键移动(1002 拖动)
        parser.Append("\u001b[<32;11;5M"u8);

        Assert.True(parser.TryParse(out var input));
        var mouse = input.Mouse!.Value;
        Assert.True(mouse.IsMove);
        Assert.True(mouse.IsDrag);
        Assert.Equal(0, mouse.Button);
        Assert.Equal(10, mouse.X);
        Assert.Equal(4, mouse.Y);
    }

    [Fact]
    public void Sgr_Release_Is_Not_Pressed()
    {
        var parser = new TerminalInputParser();
        parser.Append("\u001b[<0;11;5m"u8);

        Assert.True(parser.TryParse(out var input));
        var mouse = input.Mouse!.Value;
        Assert.False(mouse.Pressed);
        Assert.False(mouse.IsMove);
        Assert.Equal(0, mouse.Button);
    }

    [Fact]
    public void X10_Motion_Reports_Move()
    {
        var parser = new TerminalInputParser();
        // X10: Cb = 32 + 32(移动位) + 0(左键) = '@', Cx='D', Cy='-'
        parser.Append("\u001b[M@D-"u8);

        Assert.True(parser.TryParse(out var input));
        var mouse = input.Mouse!.Value;
        Assert.True(mouse.IsMove);
        Assert.True(mouse.Pressed);
        Assert.Equal(35, mouse.X);
        Assert.Equal(12, mouse.Y);
    }

    [Theory]
    [InlineData(0x03, ConsoleKey.C)]
    [InlineData(0x18, ConsoleKey.X)]
    [InlineData(0x01, ConsoleKey.A)]
    public void Control_Letter_Bytes_Carry_Control_Modifier(byte value, ConsoleKey expected)
    {
        var parser = new TerminalInputParser();
        parser.Append([value]);

        Assert.True(parser.TryParse(out var input));
        Assert.Equal(expected, input.Key.Key);
        Assert.True((input.Key.Modifiers & ConsoleModifiers.Control) != 0);
    }
}
