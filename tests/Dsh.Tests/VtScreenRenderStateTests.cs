using System.Text;

namespace Dsh.Tests;

/** VtScreen 给图形终端渲染与输入用的状态: 样式、标题、模式、查询应答, 以及全屏 TUI 依赖的控制序列。 */
public sealed class VtScreenRenderStateTests
{
    private static VtScreen Fed(string text, int width = 20, int height = 5)
    {
        var screen = new VtScreen(width, height);
        screen.Feed(Encoding.UTF8.GetBytes(text));
        return screen;
    }

    private static string RowText(VtScreen screen, int y) => Text(screen.Row(y));

    private static string Text(ReadOnlySpan<Cell> row)
    {
        var builder = new StringBuilder();
        foreach (var cell in row)
        {
            if (cell.Character != '\0')
                builder.Append(cell.Character);
        }
        return builder.ToString().TrimEnd();
    }

    [Fact]
    public void Sgr_Sets_And_Clears_Italic_Underline_Strikethrough()
    {
        var screen = Fed("\e[3;4;9mX\e[23;24;29mY");

        Assert.Equal(CellStyle.Italic | CellStyle.Underline | CellStyle.Strikethrough, screen.Row(0)[0].Style);
        Assert.Equal(CellStyle.None, screen.Row(0)[1].Style);
    }

    [Fact]
    public void Sgr_Colon_Subparameters_Set_Underline_Style_And_Truecolor()
    {
        var screen = Fed("\e[4:3mX\e[4:0mY\e[38:2::10:20:30mZ\e[48:5:196mW");

        Assert.Equal(CellStyle.Underline, screen.Row(0)[0].Style);
        Assert.Equal(CellStyle.None, screen.Row(0)[1].Style);
        Assert.Equal(CellColor.FromRgb(10, 20, 30), screen.Row(0)[2].Foreground);
        Assert.Equal(CellColor.FromRgb(255, 0, 0), screen.Row(0)[3].Background);
    }

    [Fact]
    public void Private_Prefixed_Sgr_Does_Not_Change_The_Pen()
    {
        // vim 启动时发 modifyOtherKeys(CSI > 4;2 m); 当成 SGR 会把后续文字全变粗体。
        var screen = Fed("\e[>4;2mX");

        Assert.Equal(CellStyle.None, screen.Row(0)[0].Style);
    }

    [Theory]
    [InlineData("\e]0;first title\a", "first title")]
    [InlineData("\e]2;second title\e\\", "second title")]
    public void Osc_Title_Is_Captured_With_Both_Terminators(string sequence, string expected)
    {
        var screen = Fed($"{sequence}after");

        Assert.Equal(expected, screen.Title);
        Assert.Equal("after", RowText(screen, 0));
    }

    [Fact]
    public void Alternate_Screen_Flag_Follows_Mode_1049()
    {
        var screen = Fed("\e[?1049h");
        Assert.True(screen.IsAlternateScreen);

        screen.Feed("\e[?1049l"u8);
        Assert.False(screen.IsAlternateScreen);
    }

    [Fact]
    public void Alternate_Screen_Has_Its_Own_Saved_Cursor_And_Pen()
    {
        var screen = Fed("\e[32m\e[2;3H\e7\e[3;6H\e[?1049h\e[31m\e[4;8H\e7\e[1;1H\e8X");
        Assert.Equal('X', screen.Row(3)[7].Character);
        Assert.Equal(CellColor.FromPalette(AnsiColor.Red), screen.Row(3)[7].Foreground);

        // tmux/ncurses saves its alternate-screen cursor while painting. It must not overwrite
        // either the shell cursor captured on entry or the shell's independent DECSC slot.
        screen.Feed("\e[?1049lY\e8Z");
        Assert.Equal('Y', screen.Row(2)[5].Character);
        Assert.Equal('Z', screen.Row(1)[2].Character);
        Assert.Equal(CellColor.FromPalette(AnsiColor.Green), screen.Row(2)[5].Foreground);
        Assert.Equal(CellColor.FromPalette(AnsiColor.Green), screen.Row(1)[2].Foreground);
    }

    [Fact]
    public void Autowrap_Off_Overwrites_The_Right_Edge_Without_Scrolling()
    {
        var screen = Fed("\e[?1049h\e[?7l\e[3;1H123456", width: 4, height: 3);
        Assert.Equal("1236", RowText(screen, 2));
        Assert.Equal("", RowText(screen, 1));
        screen.Feed("\e[?7hAB");
        Assert.Equal("B", RowText(screen, 2));
        Assert.Equal("123A", RowText(screen, 1));
    }

    [Theory]
    [InlineData("\e(0", "\e(B")]
    [InlineData("\e)0\u000e", "\u000f")]
    public void Dec_Special_Graphics_Maps_The_Invoked_G0_Or_G1_Character_Set(string designate, string ascii)
    {
        var screen = Fed(designate + "_`abcdefghijklmnopqrstuvwxyz{|}~" + ascii + "x", width: 40);
        Assert.Equal("\u00a0◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·x", RowText(screen, 0));
    }

    [Fact]
    public void Charset_Designation_Survives_Fragmented_Feeds_And_SI_SO_Select_Without_Printing()
    {
        var screen = Fed("\e(");
        screen.Feed("0lq");
        screen.Feed("k\e");
        screen.Feed("(Bx\e)");
        screen.Feed("0\u000ex\u000fx\u000eq");
        Assert.Equal("┌─┐x│x─", RowText(screen, 0));
        Assert.Equal(7, screen.CursorX);
    }

    [Fact]
    public void SI_SO_Inside_A_Designation_Do_Not_Consume_Its_Final_Byte()
    {
        var screen = Fed("\e)\u000e0x\e(\u000f0q");
        Assert.Equal("│─", RowText(screen, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saved_Cursor_Restores_Both_Character_Sets_And_Their_Invocation(bool useG1)
    {
        var graphics = useG1 ? "\e(B\e)0\u000e" : "\e(0\e)B\u000f";
        var other = useG1 ? "\e(0\e)B\u000f" : "\e(B\e)0\u000e";
        var screen = Fed(graphics + "\e7" + other + "x\e8lqx" + (useG1 ? "\u000f" : "\u000e") + "x");
        Assert.Equal("┌─│x", RowText(screen, 0));
        // REP repeats the already translated glyph, not a newly mapped ASCII source.
        screen.Feed((useG1 ? "\u000e" : "\u000f") + "x\e[2b");
        Assert.Equal("┌─│x│││", RowText(screen, 0));
    }

    [Fact]
    public void Full_Reset_Restores_ASCII_Character_Sets_And_Default_Invocation()
    {
        var screen = Fed("\e(0\e)0\u000e\e7\ecx\u000ex\u000fx");
        Assert.Equal("xxx", RowText(screen, 0));
        screen.Feed("\e8x\u000ex");
        Assert.Equal("xxx", RowText(screen, 0));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Snapshot_Preserves_Charset_State_Without_Retranslating_Literal_Cells(bool alternate, bool useG1)
    {
        var source = Fed("qx\e[2;1H\e(0\e)B\u000f\e7lqk\e(B", width: 8, height: 4);
        if (alternate)
            source.Feed("\e[?1049hqx");
        source.Feed("\e[3;1H\e(0\e)B\u000f\e7");
        source.Feed((useG1 ? "\e(B\e)0\u000e" : "\e(0\e)B\u000f") + "lqqqqqqk");
        var restored = Fed("\e(0\e)0\u000e", width: 8, height: 4);
        restored.Feed(source.Snapshot());
        AssertScreensEqual(source, restored);
        foreach (var continuation in new[] { "x", "\u000fx", "\e8lq", "\e[?1049lq", "\e8x", "\u000ex" })
        {
            source.Feed(continuation);
            restored.Feed(continuation);
            AssertScreensEqual(source, restored);
        }
    }

    [Theory]
    [InlineData('(', "x│x│")]
    [InlineData(')', "xx│x")]
    public void Snapshot_Preserves_A_Fragmented_Charset_Designation(char target, string expected)
    {
        var source = Fed("x\e" + target);
        var restored = new VtScreen(source.Width, source.Height);
        restored.Feed(source.Snapshot());
        const string continuation = "0x\u000ex\u000fx";
        source.Feed(continuation);
        restored.Feed(continuation);
        Assert.Equal(expected, RowText(restored, 0));
        AssertScreensEqual(source, restored);
    }

    [Theory]
    [InlineData("\e(0")]
    [InlineData("\e)0\u000e")]
    public void Snapshot_Preserves_Literal_Right_Edge_While_Graphics_Is_Selected(string graphics)
    {
        var source = Fed("abcdefgq" + graphics, width: 8, height: 3);
        var restored = new VtScreen(8, 3);
        restored.Feed(source.Snapshot());
        AssertScreensEqual(source, restored);
        Assert.Equal("abcdefgq", RowText(restored, 0));
        source.Feed("x");
        restored.Feed("x");
        Assert.Equal("│", RowText(restored, 1));
        AssertScreensEqual(source, restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_And_Subsequent_Output_Match_The_Live_Screen(bool alternate)
    {
        var source = Fed("shell\e[2;3H\e[32m\e7\e[3;4H", width: 12, height: 5);
        if (alternate)
            source.Feed("\e[?1049h\e[1;1H12345678901X\e[5;1HabcdefghijkZ");
        source.Feed("\e[2;4r\e[3;5H\e[31;44m\e7\e[3;7H\e[?1h\e[?2004h\e[?1004h\e[6 q");
        var restored = new VtScreen(source.Width, source.Height);
        restored.Feed("\e[?1049h\e[2;3r\e[?25l\e["); // reconnect may interrupt a CSI
        restored.Feed(Encoding.UTF8.GetBytes(source.Snapshot()));
        AssertScreensEqual(source, restored);

        foreach (var continuation in new[] { "\e8Q", "\r\nnext", "\e[?1049l!", "\e8R" })
        {
            source.Feed(continuation);
            restored.Feed(continuation);
            AssertScreensEqual(source, restored);
        }
    }

    [Theory]
    [InlineData("abcdef")]
    [InlineData("abcd中")]
    [InlineData("abcd\U0001F600")]
    public void Snapshot_After_Resize_Preserves_The_Right_Edge_And_Pending_Wrap(string row)
    {
        var source = Fed("\e[?1049h", width: 4, height: 2);
        source.Resize(6, 3);
        source.Feed(row);
        var restored = new VtScreen(6, 3);
        restored.Feed(Encoding.UTF8.GetBytes(source.Snapshot()));
        AssertScreensEqual(source, restored);
        source.Feed("Z");
        restored.Feed("Z");
        AssertScreensEqual(source, restored);
        Assert.Equal('Z', restored.Row(1)[0].Character);
    }

    [Theory]
    [InlineData("", TerminalMouseTracking.None, false)]
    [InlineData("", TerminalMouseTracking.None, true)]
    [InlineData("\e[?9h", TerminalMouseTracking.X10, false)]
    [InlineData("\e[?9h", TerminalMouseTracking.X10, true)]
    [InlineData("\e[?1000h", TerminalMouseTracking.Normal, false)]
    [InlineData("\e[?1000h", TerminalMouseTracking.Normal, true)]
    [InlineData("\e[?1002h", TerminalMouseTracking.ButtonEvent, false)]
    [InlineData("\e[?1002h", TerminalMouseTracking.ButtonEvent, true)]
    [InlineData("\e[?1003h", TerminalMouseTracking.AnyEvent, false)]
    [InlineData("\e[?1003h", TerminalMouseTracking.AnyEvent, true)]
    public void Snapshot_RoundTrip_PreservesNativeMouseModes(string tracking, TerminalMouseTracking expected, bool sgr)
    {
        var source = Fed($"native{tracking}{(sgr ? "\e[?1006h" : "\e[?1006h\e[?1006l")}", width: 10, height: 3);
        Assert.Equal(expected, source.MouseTracking);
        Assert.Equal(sgr, source.SgrMouse);
        var restored = Fed("\e[?1003h\e[?1006h", width: 10, height: 3);
        restored.Feed(source.Snapshot());
        AssertScreensEqual(source, restored);
        foreach (var continuation in new[] { "\e[?1000h", "\e[?1006l", "\e[?1003h\e[?1006h", "\e[?1003l", "\ec" })
        {
            source.Feed(continuation);
            restored.Feed(continuation);
            AssertScreensEqual(source, restored);
        }
    }

    [Theory]
    [InlineData("\e[?9h\e[?1006h\e[?9l\e[?1006l")]
    [InlineData("\e[?1000h\e[?1006h\e[?1000l\e[?1006l")]
    [InlineData("\e[?1002h\e[?1006h\e[?1002l\e[?1006l")]
    [InlineData("\e[?1003h\e[?1006h\e[?1003l\e[?1006l")]
    [InlineData("\e[?1003h\e[?1006h\ec")]
    public void Snapshot_RoundTrip_ClearsStaleReconnectMouseModesAfterReset(string reset)
    {
        var source = Fed(reset);
        var restored = Fed("\e[?1002h\e[?1006h");
        restored.Feed(source.Snapshot());
        AssertScreensEqual(source, restored);
        Assert.Equal(TerminalMouseTracking.None, restored.MouseTracking);
        Assert.False(restored.SgrMouse);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\e[?9h")]
    [InlineData("\e[?1002h\e[?1006l")]
    public void ConsoleSnapshot_PreservesAttachingOwnersSgrMouseNegotiation(string nativeModes)
    {
        var source = Fed($"native{nativeModes}");
        var nativeTracking = source.MouseTracking;
        var nativeSgr = source.SgrMouse;
        var owner = Fed("\e[?1003h\e[?1006h");
        owner.Feed(source.ConsoleSnapshot(wantsMouse: true));
        Assert.Equal(TerminalMouseTracking.AnyEvent, owner.MouseTracking);
        Assert.True(owner.SgrMouse);
        Assert.Equal("native", RowText(owner, 0));
        Assert.Equal(nativeTracking, source.MouseTracking);
        Assert.Equal(nativeSgr, source.SgrMouse);
    }

    private static void AssertScreensEqual(VtScreen expected, VtScreen actual)
    {
        Assert.Equal((expected.CursorX, expected.CursorY, expected.ScrollTop, expected.ScrollBottom),
            (actual.CursorX, actual.CursorY, actual.ScrollTop, actual.ScrollBottom));
        Assert.Equal(expected.IsAlternateScreen, actual.IsAlternateScreen);
        Assert.Equal(expected.CursorVisible, actual.CursorVisible);
        Assert.Equal(expected.ApplicationCursorKeys, actual.ApplicationCursorKeys);
        Assert.Equal(expected.BracketedPaste, actual.BracketedPaste);
        Assert.Equal(expected.FocusReporting, actual.FocusReporting);
        Assert.Equal(expected.MouseTracking, actual.MouseTracking);
        Assert.Equal(expected.SgrMouse, actual.SgrMouse);
        Assert.Equal(expected.CursorShape, actual.CursorShape);
        var expectedFrame = new Dsh.Gui.Terminal.TerminalFrame();
        var actualFrame = new Dsh.Gui.Terminal.TerminalFrame();
        expectedFrame.Capture(expected, 0);
        actualFrame.Capture(actual, 0);
        for (var row = 0; row < expected.Height; row++)
            Assert.Equal(expectedFrame.Row(row).ToArray(), actualFrame.Row(row).ToArray());
    }

    [Fact]
    public void Scroll_Region_Is_Exposed_Zero_Based()
    {
        var screen = Fed("\e[2;4r", height: 6);

        Assert.Equal(1, screen.ScrollTop);
        Assert.Equal(3, screen.ScrollBottom);
    }

    [Fact]
    public void Bracketed_Paste_And_Focus_Reporting_Modes_Toggle()
    {
        var screen = Fed("\e[?2004h\e[?1004h");
        Assert.True(screen.BracketedPaste);
        Assert.True(screen.FocusReporting);

        screen.Feed("\e[?2004l\e[?1004l"u8);
        Assert.False(screen.BracketedPaste);
        Assert.False(screen.FocusReporting);
    }

    [Theory]
    [InlineData("\e[?9h", TerminalMouseTracking.X10)]
    [InlineData("\e[?1000h", TerminalMouseTracking.Normal)]
    [InlineData("\e[?1002h", TerminalMouseTracking.ButtonEvent)]
    [InlineData("\e[?1003h", TerminalMouseTracking.AnyEvent)]
    [InlineData("\e[?1002h\e[?1002l", TerminalMouseTracking.None)]
    public void Mouse_Tracking_Mode_Follows_Decset(string sequence, TerminalMouseTracking expected)
        => Assert.Equal(expected, Fed(sequence).MouseTracking);

    [Fact]
    public void Sgr_Mouse_Encoding_Toggles()
    {
        var screen = Fed("\e[?1006h");
        Assert.True(screen.SgrMouse);

        screen.Feed("\e[?1006l"u8);
        Assert.False(screen.SgrMouse);
    }

    [Theory]
    [InlineData("\e[2 q", TerminalCursorShape.Block)]
    [InlineData("\e[4 q", TerminalCursorShape.Underline)]
    [InlineData("\e[6 q", TerminalCursorShape.Bar)]
    [InlineData("\e[6 q\e[0 q", TerminalCursorShape.Default)]
    public void Cursor_Shape_Follows_Decscusr(string sequence, TerminalCursorShape expected)
        => Assert.Equal(expected, Fed(sequence).CursorShape);

    [Fact]
    public void Cursor_Position_Report_Reflects_The_Cursor_At_Query_Time()
    {
        var screen = new VtScreen(20, 5);
        var replies = new List<string>();
        screen.Reply += replies.Add;

        screen.Feed("\e[3;7H\e[6nmoved"u8);

        Assert.Equal(["\e[3;7R"], replies);
    }

    [Fact]
    public void Device_Attribute_And_Status_Queries_Are_Answered()
    {
        var screen = new VtScreen(20, 5);
        var replies = new List<string>();
        screen.Reply += replies.Add;

        screen.Feed("\e[c\e[>c\e[5n"u8);

        Assert.Equal(3, replies.Count);
        Assert.StartsWith("\e[?", replies[0], StringComparison.Ordinal);
        Assert.EndsWith("c", replies[0], StringComparison.Ordinal);
        Assert.StartsWith("\e[>", replies[1], StringComparison.Ordinal);
        Assert.Equal("\e[0n", replies[2]);
    }

    [Fact]
    public void Color_Queries_Are_Answered_Only_When_Colors_Are_Known()
    {
        var screen = new VtScreen(20, 5);
        var replies = new List<string>();
        screen.Reply += replies.Add;

        screen.Feed("\e]11;?\a"u8);
        Assert.Empty(replies);

        screen.QueryForeground = CellColor.FromRgb(0xcc, 0xcc, 0xcc);
        screen.QueryBackground = CellColor.FromRgb(0x12, 0x34, 0x56);
        screen.Feed("\e]11;?\e\\\e]10;?\a"u8);
        Assert.Equal(["\e]11;rgb:1212/3434/5656\e\\", "\e]10;rgb:cccc/cccc/cccc\e\\"], replies);
    }

    [Theory]
    [InlineData("\eP+q544e\e\\X")]
    [InlineData("\e_Gf=24;AAAA\e\\X")]
    [InlineData("\e[>1u\e[?uX")]
    public void String_And_Keyboard_Protocol_Sequences_Do_Not_Print(string sequence)
    {
        var screen = Fed(sequence);

        Assert.Equal("X", RowText(screen, 0));
        Assert.Equal(1, screen.CursorX);
    }

    [Fact]
    public void Kitty_Keyboard_Query_Does_Not_Restore_The_Cursor()
    {
        var screen = Fed("\e[2;2H\e[s\e[4;6H\e[?u");

        Assert.Equal(5, screen.CursorX);
        Assert.Equal(3, screen.CursorY);
    }

    [Fact]
    public void Repeat_Preceding_Character_Expands()
    {
        Assert.Equal("aaaa", RowText(Fed("a\e[3b"), 0));
    }

    [Fact]
    public void Insert_Mode_Shifts_Existing_Text_Right()
    {
        Assert.Equal("Xabc", RowText(Fed("abc\r\e[4hX\e[4l"), 0));
    }

    [Fact]
    public void Alternate_Screen_Scrolling_Does_Not_Grow_Scrollback()
    {
        var screen = Fed("\e[?1049h" + string.Concat(Enumerable.Repeat("line\r\n", 10)), height: 3);

        Assert.Equal(0, screen.ScrollbackCount);
    }

    [Fact]
    public void Erase_Display_Mode_3_Clears_Scrollback_Only()
    {
        var screen = Fed(string.Concat(Enumerable.Repeat("line\r\n", 10)) + "keep", height: 3);
        Assert.True(screen.ScrollbackCount > 0);

        screen.Feed("\e[3J"u8);

        Assert.Equal(0, screen.ScrollbackCount);
        Assert.Equal("keep", RowText(screen, 2));
    }

    [Fact]
    public void Erase_Uses_The_Current_Background_Color()
    {
        var screen = Fed("\e[44m\e[2J");

        Assert.Equal(CellColor.FromPalette(AnsiColor.Blue), screen.Row(3)[10].Background);
    }

    [Fact]
    public void Shrinking_Keeps_The_Cursor_Line_And_Pushes_Top_Rows_To_Scrollback()
    {
        var screen = Fed("one\r\ntwo\r\nthree\r\nfour\r\nfive", height: 5);

        screen.Resize(20, 3);

        Assert.Equal("five", RowText(screen, 2));
        Assert.Equal(2, screen.CursorY);
        Assert.Equal(2, screen.ScrollbackCount);
        Assert.Equal("two", Text(screen.ScrollbackRow(0)));
    }

    [Fact]
    public void Zero_Width_Marks_And_Delete_Do_Not_Take_A_Cell()
    {
        Assert.Equal("ex", RowText(Fed("é‍\u007fx"), 0));
    }

    [Fact]
    public void Wide_Character_At_Last_Column_Wraps_To_Next_Line()
    {
        var screen = Fed("abc中", width: 4);

        Assert.Equal("abc", RowText(screen, 0));
        Assert.Equal('中', screen.Row(1)[0].Character);
        Assert.Equal('\0', screen.Row(1)[1].Character);
    }

    [Fact]
    public void Overwriting_Half_Of_A_Wide_Character_Clears_The_Other_Half()
    {
        var screen = Fed("中文\e[1;2HX");

        Assert.Equal(' ', screen.Row(0)[0].Character);
        Assert.Equal('X', screen.Row(0)[1].Character);
        Assert.Equal('文', screen.Row(0)[2].Character);
    }

    [Fact]
    public void LineAt_Indexes_Screen_From_Zero_And_Scrollback_Below_Zero()
    {
        var screen = Fed("old\r\nmid\r\nnew", height: 2);

        Assert.Equal("mid", Text(screen.LineAt(0)));
        Assert.Equal("new", Text(screen.LineAt(1)));
        Assert.Equal("old", Text(screen.LineAt(-1)));
    }
}
