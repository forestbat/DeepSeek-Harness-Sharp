using Dsh.Tui;

namespace Dsh.Tests;

/** 命令结果浮层: 非模态键位与滚动边界。 */
public sealed class ResultOverlayTests
{
    [Fact]
    public void Show_SplitsLines_AndKeepsText()
    {
        var overlay = new ResultOverlay();
        overlay.Show("/mcp 结果", "a\nb\r\nc", isError: false);
        Assert.True(overlay.IsActive);
        Assert.Equal(["a", "b", "c"], overlay.Lines);
        Assert.Equal("/mcp 结果", overlay.Title);
    }

    [Fact]
    public void PrintableKeys_FallThrough_SoTheUserCanKeepTyping()
    {
        var overlay = new ResultOverlay();
        overlay.Show("t", "a\nb", isError: false);
        // 非模态: 可打印字符不归浮层, 必须落到输入框。
        Assert.False(overlay.HandleKey(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false)));
        Assert.True(overlay.IsActive);
    }

    [Fact]
    public void NavigationKeys_ScrollWithinBounds_AndEscapeCloses()
    {
        var overlay = new ResultOverlay();
        overlay.Show("t", string.Join('\n', Enumerable.Range(0, 40).Select(index => $"line {index}")), isError: false);
        overlay.VisibleRows = 10;

        overlay.MoveUp();
        Assert.Equal(0, overlay.First);
        overlay.MoveDown(5);
        Assert.Equal(5, overlay.First);
        overlay.MoveEnd();
        Assert.Equal(30, overlay.First);
        overlay.MoveDown(100);
        Assert.Equal(30, overlay.First);
        overlay.MoveHome();
        Assert.Equal(0, overlay.First);

        Assert.True(overlay.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)));
        Assert.False(overlay.IsActive);
    }

    [Fact]
    public void PageKeys_MoveByVisibleRows()
    {
        var overlay = new ResultOverlay();
        overlay.Show("t", string.Join('\n', Enumerable.Range(0, 60).Select(index => $"line {index}")), isError: false);
        overlay.VisibleRows = 12;
        Assert.True(overlay.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.PageDown, false, false, false)));
        Assert.Equal(12, overlay.First);
        Assert.True(overlay.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false)));
        Assert.Equal(0, overlay.First);
    }

    [Fact]
    public void Close_ClearsState_SoTheNextCommandCanReuseIt()
    {
        var overlay = new ResultOverlay();
        overlay.Show("t", "a\nb", isError: true);
        Assert.True(overlay.IsError);
        overlay.Close();
        Assert.False(overlay.IsActive);
        Assert.Empty(overlay.Lines);
        Assert.Equal("", overlay.Text);
    }
}
