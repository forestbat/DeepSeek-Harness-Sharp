using Dsh.PtyTerminal;

namespace Dsh.Tests;

/**
 * 只针对环缓冲的可独立重用逻辑: 跨尾拷贝、行数/字节预算裁剪、Read 的 offset/count 语义。
 * 测试只组装输入并断言输出, 不复制实现。
 */
public class BoundedTextBufferTests
{
    [Fact]
    public void AppendWrapsAcrossTheRingTailAndKeepsOrder()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 8);
        buffer.Append("abcde");
        buffer.Append("fghij");
        Assert.Equal("cdefghij", buffer.Snapshot().Text);
        Assert.True(buffer.Snapshot().Truncated);
    }

    [Fact]
    public void SmallChunksAcrossManyWrapsKeepTheTailIntact()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 10);
        for (var index = 0; index < 100; index++)
            buffer.Append(index.ToString());
        Assert.Equal("9596979899", buffer.Snapshot().Text);
    }

    [Fact]
    public void MaxLinesEvictsCompleteLeadingLines()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 1024, maxLines: 3);
        buffer.Append("a\nb\nc\nd\ne");
        Assert.Equal("c\nd\ne", buffer.Snapshot().Text);
        Assert.True(buffer.Snapshot().Truncated);
    }

    [Fact]
    public void MaxLinesCountsTrailingEmptyLine()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 1024, maxLines: 2);
        buffer.Append("a\nb\n");
        Assert.Equal("b\n", buffer.Snapshot().Text);
    }

    [Fact]
    public void ConsumeReturnsAndClearsOnlyLiveContent()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 16);
        buffer.Append("first\n");
        var consumed = buffer.Consume();
        Assert.Equal("first\n", consumed.Delta);
        Assert.False(consumed.Truncated);
        Assert.Equal("", buffer.Snapshot().Text);
        buffer.Append("second");
        Assert.Equal("second", buffer.Snapshot().Text);
    }

    [Fact]
    public void ByteBudgetDropsWholeMultibyteRunes()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 5);
        buffer.Append("ééé");
        var snapshot = buffer.Snapshot();
        Assert.Equal("éé", snapshot.Text);
        Assert.True(snapshot.Truncated);
    }

    [Fact]
    public void ByteBudgetNeverLeavesALoneSurrogate()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 5);
        buffer.Append("😀😀");
        var snapshot = buffer.Snapshot();
        Assert.Equal("😀", snapshot.Text);
        Assert.True(snapshot.Truncated);
    }

    [Fact]
    public void ReadReturnsLinesCountedFromTheTail()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 1024);
        buffer.Append("l0\nl1\nl2\nl3\nl4");

        var tail = buffer.Read(offset: 0, count: 2, maxBytes: 1024);
        Assert.Equal("l3\nl4", tail.Text);
        Assert.Equal(5, tail.TotalLines);
        Assert.Equal(0, tail.LineBegin);
        Assert.Equal(2, tail.LineEnd);
        Assert.False(tail.Truncated);

        var middle = buffer.Read(offset: 1, count: 2, maxBytes: 1024);
        Assert.Equal("l2\nl3", middle.Text);
        Assert.Equal(1, middle.LineBegin);
        Assert.Equal(3, middle.LineEnd);

        var head = buffer.Read(offset: 3, count: 2, maxBytes: 1024);
        Assert.Equal("l0\nl1", head.Text);
        Assert.Equal(3, head.LineBegin);
        Assert.Equal(5, head.LineEnd);
    }

    [Fact]
    public void ReadPastTotalLinesReturnsEmptyAtTheRequestedOffset()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 1024);
        buffer.Append("only");
        var result = buffer.Read(offset: 4, count: 10, maxBytes: 1024);
        Assert.Equal("", result.Text);
        Assert.Equal(1, result.TotalLines);
        Assert.Equal(4, result.LineBegin);
        Assert.Equal(4, result.LineEnd);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void ReadKeepsTrailingNewlineWhenTheLastLineIsEmpty()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 1024);
        buffer.Append("a\nb\n");
        var result = buffer.Read(offset: 0, count: 5, maxBytes: 1024);
        Assert.Equal("a\nb\n", result.Text);
        Assert.Equal(3, result.TotalLines);
        Assert.Equal(3, result.LineEnd);
    }

    [Fact]
    public void ReadTruncatesToTheByteBudgetFromTheTail()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 1024);
        buffer.Append("aaaa\nbbbb");
        var result = buffer.Read(offset: 0, count: 2, maxBytes: 5);
        Assert.Equal("\nbbbb", result.Text);
        Assert.Equal(2, result.LineEnd);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void ReadReportsTruncationFromEviction()
    {
        var buffer = new TerminalOutputBuffer(maxBytes: 8);
        buffer.Append("abcde");
        buffer.Append("fghij");
        var result = buffer.Read(offset: 0, count: 10, maxBytes: 8);
        Assert.Equal("cdefghij", result.Text);
        Assert.True(result.Truncated);
    }
}
