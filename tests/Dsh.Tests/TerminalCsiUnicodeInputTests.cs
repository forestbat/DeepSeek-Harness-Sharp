using System.Text;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * 中文标点在 kitty(CSI-u)与 xterm modifyOtherKeys 两种键盘协议下的解码。
 * 两种协议都把 codepoint 与修饰键塞进 CSI 参数, 终止符分别是 `u` 与 `~`(`27;修饰;codepoint~`)。
 */
public class TerminalCsiUnicodeInputTests
{
    private static TerminalInputParser Parser(string sequence)
    {
        var parser = new TerminalInputParser();
        parser.Append(Encoding.ASCII.GetBytes(sequence));
        return parser;
    }

    [Theory]
    [InlineData(0x2018)]
    [InlineData(0x201c)]
    [InlineData(0x201d)]
    [InlineData(0x2026)]
    [InlineData(0x2014)]
    public void CsiU_Alt_Modified_Punctuation_Becomes_Text_Key(int codePoint)
    {
        var parser = Parser($"\u001b[{codePoint};3u");

        Assert.True(parser.TryParse(out var input));
        Assert.Equal((char)codePoint, input.Key.KeyChar);
        Assert.True((input.Key.Modifiers & ConsoleModifiers.Alt) != 0);
        Assert.False((input.Key.Modifiers & ConsoleModifiers.Control) != 0);
        Assert.False((input.Key.Modifiers & ConsoleModifiers.Shift) != 0);
    }

    /** 报告里的样例码点 8216 是 U+2018 ‘, 左双引号 “ 是 8220(U+201C); 两者走同一条解码路径。 */
    [Fact]
    public void CsiU_Sample_CodePoint_8216_Decodes_To_Its_Own_Character()
    {
        var parser = Parser("\u001b[8216;3u");

        Assert.True(parser.TryParse(out var input));
        Assert.Equal('‘', input.Key.KeyChar);
    }

    [Theory]
    [InlineData("\u001b[27;5;8216~", '‘')]
    [InlineData("\u001b[27;5;8220~", '“')]
    public void ModifyOtherKeys_Ctrl_Modified_Punctuation_Becomes_Text_Key(string sequence, char expected)
    {
        var parser = Parser(sequence);

        Assert.True(parser.TryParse(out var input));
        Assert.Equal(expected, input.Key.KeyChar);
        Assert.True((input.Key.Modifiers & ConsoleModifiers.Control) != 0);
        Assert.False((input.Key.Modifiers & ConsoleModifiers.Alt) != 0);
    }

    [Fact]
    public void CsiU_Split_Across_Appends_Is_Parsed()
    {
        var parser = new TerminalInputParser();
        parser.Append("\u001b[822"u8);

        Assert.False(parser.TryParse(out _));
        parser.Append("0;3u"u8);

        Assert.True(parser.TryParse(out var input));
        Assert.Equal('“', input.Key.KeyChar);
        Assert.True((input.Key.Modifiers & ConsoleModifiers.Alt) != 0);
    }

    [Fact]
    public void CsiU_Release_Event_Is_Dropped_And_Following_Bytes_Survive()
    {
        var parser = Parser("\u001b[8220;3:3u");

        Assert.False(parser.TryParse(out _));
        parser.Append("z"u8);

        Assert.True(parser.TryParse(out var input));
        Assert.Equal('z', input.Key.KeyChar);
    }

    [Fact]
    public void CsiU_Repeat_Event_Is_Dropped_And_Following_Bytes_Survive()
    {
        var parser = Parser("\u001b[8220;3:2u");

        Assert.False(parser.TryParse(out _));
        parser.Append("y"u8);

        Assert.True(parser.TryParse(out var input));
        Assert.Equal('y', input.Key.KeyChar);
    }

    [Fact]
    public void CsiU_Ctrl_Letter_Maps_To_Named_Key_Without_Character()
    {
        var parser = Parser("\u001b[99;5u");

        Assert.True(parser.TryParse(out var input));
        Assert.Equal(ConsoleKey.C, input.Key.Key);
        Assert.Equal('\0', input.Key.KeyChar);
        Assert.True((input.Key.Modifiers & ConsoleModifiers.Control) != 0);
    }

    [Fact]
    public void Unknown_Csi_Yields_No_Key_And_Keeps_Following_Text()
    {
        var parser = Parser("\u001b[1;2;3Z");

        Assert.False(parser.TryParse(out _));
        parser.Append("ab"u8);

        Assert.True(parser.TryParse(out var first));
        Assert.Equal('a', first.Key.KeyChar);
        Assert.True(parser.TryParse(out var second));
        Assert.Equal('b', second.Key.KeyChar);
    }

    [Fact]
    public void Up_Arrow_Csi_Is_Still_Parsed()
    {
        var parser = Parser("\u001b[A");

        Assert.True(parser.TryParse(out var input));
        Assert.Equal(ConsoleKey.UpArrow, input.Key.Key);
    }

    [Fact]
    public void F_Key_And_Modified_Delete_Are_Still_Parsed()
    {
        var parser = Parser("\u001b[15~");
        Assert.True(parser.TryParse(out var function));
        Assert.Equal(ConsoleKey.F5, function.Key.Key);

        parser.Append("\u001b[3;5~"u8);
        Assert.True(parser.TryParse(out var delete));
        Assert.Equal(ConsoleKey.Delete, delete.Key.Key);
    }

    [Fact]
    public void Bracketed_Paste_Is_Still_A_Paste_Event()
    {
        var parser = Parser("\u001b[200~abc\u001b[201~");

        Assert.True(parser.TryParse(out var input));
        Assert.True(input.IsPaste);
        Assert.Equal("abc", input.Paste);
    }

    [Fact]
    public void Sgr_Mouse_Report_Is_Still_Parsed()
    {
        var parser = Parser("\u001b[<0;1;2M");

        Assert.True(parser.TryParse(out var input));
        Assert.True(input.IsMouse);
        var mouse = input.Mouse!.Value;
        Assert.Equal(0, mouse.Button);
        Assert.True(mouse.Pressed);
        Assert.Equal(0, mouse.X);
        Assert.Equal(1, mouse.Y);
    }

    [Fact]
    public void Lone_Escape_Is_Still_Held_Then_Flushed()
    {
        var parser = new TerminalInputParser();
        parser.Append([0x1b]);

        Assert.True(parser.HasPendingEscape);
        Assert.False(parser.TryParse(out _));
        Assert.True(parser.TryFlushPendingEscape(out var input));
        Assert.Equal(ConsoleKey.Escape, input.Key.Key);
    }
}
