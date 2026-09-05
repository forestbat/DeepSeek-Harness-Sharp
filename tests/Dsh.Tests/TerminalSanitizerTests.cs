using Dsh.Terminal;

namespace Dsh.Tests;

public class TerminalSanitizerTests
{
    [Fact]
    public void RemovesSplitCsiAndOwnedOscPromptMarkers()
    {
        var sanitizer = new TerminalSanitizer(64);
        var first = sanitizer.Push("red\u001b[3");
        Assert.Equal("red", first.Text);
        Assert.False(first.Prompt);
        var second = sanitizer.Push("1m text\u001b[0m\r\n");
        Assert.Equal(" text\n", second.Text);
        var third = sanitizer.Push("\u001b]133;");
        Assert.Equal("", third.Text);
        var fourth = sanitizer.Push("D;0\u0007dsh> ");
        Assert.Equal("dsh> ", fourth.Text);
        Assert.True(fourth.Prompt);
        Assert.Equal("dsh> ", fourth.PromptTail);
    }

    [Fact]
    public void DropsUnrelatedOscShortEscapesBelAndIncompleteTrailingEscape()
    {
        var sanitizer = new TerminalSanitizer(64);
        Assert.Equal("abc", sanitizer.Push("a\u001b]0;title\u001b\\b\u001b7c\u0007").Text);
        Assert.Equal("tail", sanitizer.Push("tail\u001b").Text);
        Assert.Equal("", sanitizer.Flush());
        Assert.Equal("", sanitizer.Flush());
        Assert.Equal("middle", sanitizer.Push("\u001b]0;one\u0007middle\u001b\\").Text);
        Assert.Equal("middle", sanitizer.Push("\u001b]0;one\u001b\\middle\u0007").Text);
        Assert.Equal("", sanitizer.Push("\u001b]0;title\u001b\\").Text);
    }

    [Fact]
    public void NormalizesCrlfAndStandaloneCarriageReturns()
    {
        Assert.Equal("a\nb\nc", TerminalText.NormalizeTerminalText("a\r\nb\rc\x07"));
    }

    [Fact]
    public void CarriesTrailingCarriageReturnAcrossChunksAndFlushesStandaloneCr()
    {
        var sanitizer = new TerminalSanitizer(64);
        Assert.Equal("a", sanitizer.Push("a\r").Text);
        Assert.Equal("\nb", sanitizer.Push("\nb").Text);
        Assert.Equal("", sanitizer.Push("\r").Text);
        Assert.Equal("\n", sanitizer.Flush());
    }

    [Fact]
    public void ReportsPrintablePromptTextFollowingMarkerInLaterChunk()
    {
        var sanitizer = new TerminalSanitizer(64);
        var marker = sanitizer.Push("\u001b]133;D;0\u0007");
        Assert.True(marker.Prompt);
        Assert.Equal("", marker.PromptTail);
        var tail = sanitizer.Push("dsh> ");
        Assert.Equal("dsh> ", tail.Text);
        Assert.Equal("dsh> ", tail.PromptTail);
    }

    [Fact]
    public void BoundsAndDiscardsUnterminatedControlSequences()
    {
        var oscBel = new TerminalSanitizer(8);
        Assert.Equal("", oscBel.Push($"\x1b]0;{new string('x', 16)}").Text);
        Assert.Equal("tail", oscBel.Push("more\x07tail").Text);

        var csi = new TerminalSanitizer(8);
        Assert.Equal("", csi.Push($"\x1b[{new string('1', 16)}").Text);
        Assert.Equal("", csi.Push("123").Text);
        Assert.Equal("text", csi.Push("mtext").Text);
    }
}
