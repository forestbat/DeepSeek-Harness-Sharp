using Dsh.Pty;
using Dsh.Tui;

namespace Dsh.Tests;

public sealed class PopupListTests
{
    private static readonly ConsoleRect Area = new(0, 0, 40, 16);

    [Fact]
    public void WindowOf_ScrollsSelectionIntoView()
    {
        var window = PopupList.WindowOf(Area, 0, 100, 99);

        Assert.Equal(14, window.Visible);
        Assert.Equal(86, window.First);
    }

    [Fact]
    public void WindowOf_EmptyListKeepsOneEmptyRow()
    {
        var window = PopupList.WindowOf(Area, 0, 0, 0);

        Assert.Equal(3, window.Height);
        Assert.Equal(1, window.Visible);
    }

    [Fact]
    public void ScrollbarOf_KeepsThumbWithinTrack()
    {
        var window = PopupList.WindowOf(Area, 0, 100, 0);
        var bar = PopupList.ScrollbarOf(window, 100);

        Assert.NotNull(bar);
        Assert.Equal(window.X + window.Width - 2, bar!.Column);
        Assert.Equal(window.Visible, bar.TrackHeight);
        Assert.InRange(bar.ThumbHeight, 1, bar.TrackHeight);
        Assert.InRange(bar.ThumbTop, 0, bar.TrackHeight - bar.ThumbHeight);
        Assert.Equal(86, bar.MaxFirst);
    }

    [Fact]
    public void ScrollbarOf_NullWhenItemsFit()
    {
        var window = PopupList.WindowOf(Area, 0, 3, 0);

        Assert.Null(PopupList.ScrollbarOf(window, 3));
    }
}
