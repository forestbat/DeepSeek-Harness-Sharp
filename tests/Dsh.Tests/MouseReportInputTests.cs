using System.Text;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * 鼠标报文**永远不能**污染输入: SGR / X10、带前导 ESC / 被 ConPTY 吃掉 ESC、以及在任意读边界被切开,
 * 都必须整条被识别(丢弃或还原成鼠标事件), 一个报文字节都不许落成"键入文本"。
 * 这里同时覆盖字节路径(TerminalInputParser)与 Windows 控制台记录路径(MouseReportKeyGuard)。
 */
public class MouseReportInputTests
{
    /// X10 左键点击(40,13): ESC [ M + Cb(32) Cx('H'=32+40) Cy('-'=32+13)
    private const string X10Press = "\u001b[M H-";

    /// SGR 左键拖拽: CSI < 32;11;5 M
    private const string SgrDrag = "\u001b[<32;11;5M";

    /// SGR 滚轮上滚: CSI < 64;3;2 M
    private const string SgrWheel = "\u001b[<64;3;2M";

    /// SGR 释放: CSI < 0;3;3 m
    private const string SgrRelease = "\u001b[<0;3;3m";

    public static TheoryData<string> Reports() => new() { X10Press, SgrDrag, SgrWheel, SgrRelease };

    [Theory]
    [MemberData(nameof(Reports))]
    public void Report_Split_At_Every_Boundary_Never_Leaks_Text(string report)
    {
        var bytes = Encoding.ASCII.GetBytes(report);
        for (var split = 1; split < bytes.Length; split++)
        {
            var parser = new TerminalInputParser();
            var leaks = new List<char>();
            var mice = 0;
            parser.Append(bytes.AsSpan(0, split));
            Drain(parser, leaks, ref mice);
            parser.Append(bytes.AsSpan(split));
            Drain(parser, leaks, ref mice);

            Assert.True(leaks.Count == 0, $"split={split} 泄漏了报文字节: {new string(leaks.ToArray())}");
            Assert.True(mice == 1, $"split={split} 应当还原出 1 个鼠标事件, 实际 {mice}");

            // 报文后紧跟一个正常按键: 必须原样通过(说明过滤器没有吞掉后续输入)
            var tail = new List<char>();
            parser.Append("z"u8);
            Drain(parser, tail, ref mice);
            Assert.Equal("z", new string(tail.ToArray()));
        }
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void Report_Without_Leading_Escape_Never_Leaks_Text(string report)
    {
        // ConPTY 的输入侧会吃掉前导 ESC: 只剩 `[<…M` / `[M`+3 时也必须整条识别
        var orphan = report[1..];
        var parser = new TerminalInputParser();
        var leaks = new List<char>();
        var mice = 0;
        parser.Append(Encoding.ASCII.GetBytes(orphan));
        Drain(parser, leaks, ref mice);

        Assert.True(leaks.Count == 0, $"无 ESC 报文泄漏了字节: {new string(leaks.ToArray())}");
        Assert.True(mice == 1, $"无 ESC 报文应当还原出 1 个鼠标事件, 实际 {mice}");

        var tail = new List<char>();
        parser.Append("z"u8);
        Drain(parser, tail, ref mice);
        Assert.Equal("z", new string(tail.ToArray()));
    }

    [Fact]
    public void Orphan_Report_Split_Byte_By_Byte_Never_Leaks_Text()
    {
        var orphan = SgrDrag[1..];
        var parser = new TerminalInputParser();
        var leaks = new List<char>();
        var mice = 0;
        foreach (var value in Encoding.ASCII.GetBytes(orphan))
        {
            parser.Append([value]);
            Drain(parser, leaks, ref mice);
        }

        Assert.Empty(leaks);
        Assert.Equal(1, mice);
    }

    [Fact]
    public void Lone_Escape_Is_Held_Until_Quiet_Then_Reported()
    {
        var parser = new TerminalInputParser();
        parser.Append([0x1b]);

        Assert.True(parser.HasPendingEscape);
        Assert.False(parser.TryParse(out _));
        Assert.True(parser.TryFlushPendingEscape(out var escape));
        Assert.Equal(ConsoleKey.Escape, escape.Key.Key);
        Assert.False(parser.HasPendingEscape);
    }

    [Fact]
    public void Escape_Then_Report_Is_Still_Swallowed()
    {
        // ESC 先到(比如按 Esc), 紧接着来的报文只要是完整的也整条吃掉 —— 不能因为 ESC 已被当成按键就漏报文
        var parser = new TerminalInputParser();
        parser.Append([0x1b]);
        Assert.True(parser.TryFlushPendingEscape(out _));

        var leaks = new List<char>();
        var mice = 0;
        parser.Append(Encoding.ASCII.GetBytes(X10Press));
        Drain(parser, leaks, ref mice);

        Assert.Empty(leaks);
        Assert.Equal(1, mice);
    }

    [Theory]
    [InlineData("\u001b[A", ConsoleKey.UpArrow)]
    [InlineData("\u001b[5~", ConsoleKey.PageUp)]
    public void Arrow_And_Function_Sequences_Still_Parse(string sequence, ConsoleKey expected)
    {
        var parser = new TerminalInputParser();
        parser.Append(Encoding.ASCII.GetBytes(sequence));

        Assert.True(parser.TryParse(out var input));
        Assert.False(input.IsMouse);
        Assert.Equal(expected, input.Key.Key);
    }

    [Fact]
    public void Bracket_And_Text_Still_Type_Through()
    {
        var parser = new TerminalInputParser();
        parser.Append("a["u8);
        parser.Append("x"u8);

        Assert.True(parser.TryParse(out var first));
        Assert.Equal('a', first.Key.KeyChar);
        Assert.True(parser.TryParse(out var second));
        Assert.Equal('[', second.Key.KeyChar);
        Assert.True(parser.TryParse(out var third));
        Assert.Equal('x', third.Key.KeyChar);
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void Key_Path_Guard_Swallows_Report_Without_Escape(string report)
    {
        // Windows 记录路径: ConPTY 吃掉 ESC 后, 报文字节以字符按键的形式到达
        var guard = new MouseReportKeyGuard();
        var produced = new List<TerminalInputEvent>();
        foreach (var character in report[1..])
            produced.AddRange(guard.Accept(new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false)));

        Assert.Contains(produced, item => item.IsMouse);
        Assert.DoesNotContain(produced, item => !item.IsMouse);

        produced.AddRange(guard.Accept(new ConsoleKeyInfo('z', ConsoleKey.NoName, false, false, false)));
        Assert.Contains(produced, item => !item.IsMouse && item.Key.KeyChar == 'z');
        Assert.False(guard.HasPending);
    }

    [Fact]
    public void Key_Path_Guard_Flush_Returns_A_Real_Escape()
    {
        var guard = new MouseReportKeyGuard();
        var start = new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false);

        Assert.True(MouseReportKeyGuard.MayStart(start));
        Assert.Empty(guard.Accept(start));
        Assert.True(guard.HasPending);

        var flushed = guard.Flush();
        Assert.Single(flushed);
        Assert.Equal(ConsoleKey.Escape, flushed[0].Key.Key);
    }

    [Theory]
    [InlineData("\u001b[?1000h")]
    [InlineData("\u001b[?1002h")]
    [InlineData("\u001b[?1006h")]
    public void Mouse_Enable_Sequences_Are_Dropped_At_Every_Split(string sequence)
    {
        var bytes = Encoding.ASCII.GetBytes($"AB{sequence}CD");
        for (var split = 1; split < bytes.Length; split++)
        {
            var filter = new Dsh.Pty.MouseModeFilter();
            var output = new List<byte>();
            output.AddRange(filter.Filter(bytes.AsSpan(0, split)));
            output.AddRange(filter.Filter(bytes.AsSpan(split)));

            Assert.Equal("ABCD", Encoding.ASCII.GetString(output.ToArray()));
        }
    }

    /** 关闭序列必须放行: attach 时要靠它复位"上一次运行把终端留在鼠标模式"的终端。 */
    [Theory]
    [InlineData("\u001b[?1000l")]
    [InlineData("\u001b[?1002l")]
    [InlineData("\u001b[?1006l")]
    public void Mouse_Disable_Sequences_Are_Kept_At_Every_Split(string sequence)
    {
        var bytes = Encoding.ASCII.GetBytes($"AB{sequence}CD");
        for (var split = 1; split < bytes.Length; split++)
        {
            var filter = new Dsh.Pty.MouseModeFilter();
            var output = new List<byte>();
            output.AddRange(filter.Filter(bytes.AsSpan(0, split)));
            output.AddRange(filter.Filter(bytes.AsSpan(split)));

            Assert.Equal($"AB{sequence}CD", Encoding.ASCII.GetString(output.ToArray()));
        }
    }

    [Theory]
    [InlineData("\u001b[?25l")]
    [InlineData("\u001b[?1049h")]
    [InlineData("\u001b[?2026h")]
    [InlineData("\u001b[?2004h")]
    public void Non_Mouse_Modes_Are_Kept(string sequence)
    {
        var filter = new Dsh.Pty.MouseModeFilter();
        var output = filter.Filter(Encoding.ASCII.GetBytes($"A{sequence}B"));

        Assert.Equal($"A{sequence}B", Encoding.ASCII.GetString(output));
    }

    private static void Drain(TerminalInputParser parser, List<char> leaked, ref int mice)
    {
        while (parser.TryParse(out var input))
        {
            if (input.IsMouse)
            {
                mice++;
                continue;
            }

            if (input.Key.KeyChar != '\0')
                leaked.Add(input.Key.KeyChar);
        }
    }
}
