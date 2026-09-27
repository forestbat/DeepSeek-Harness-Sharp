using Dsh.Tui;
using Xunit;

namespace Dsh.Tests;

/** GPU 窗口输入行光标: 闪烁相位与"反显光标格"的叠加规则(窗口里没有宿主终端光标可用, 必须自己画)。 */
public sealed class GpuRendererCursorTests
{
    [Fact]
    public void Cursor_Blink_Phase_Alternates_Every_Half_Period()
    {
        Assert.True(GpuRenderer.IsCursorLit(0));
        Assert.True(GpuRenderer.IsCursorLit(499));
        Assert.False(GpuRenderer.IsCursorLit(500));
        Assert.False(GpuRenderer.IsCursorLit(999));
        Assert.True(GpuRenderer.IsCursorLit(1000));
    }

    [Fact]
    public void Cursor_Overlay_Reverses_Caret_Cell_And_Ignores_Hidden_Or_OutOfRange()
    {
        var grid = new CellGrid(10, 4);
        GpuRenderer.DrawCursor(grid, 3, 2, visible: true);

        Assert.True((grid[3, 2].Style & CellStyle.Reverse) != 0);
        Assert.Equal(CellStyle.None, grid[2, 2].Style);
        Assert.Equal(CellStyle.None, grid[3, 1].Style);

        var plain = new CellGrid(10, 4);
        GpuRenderer.DrawCursor(plain, 3, 2, visible: false);
        Assert.Equal(CellStyle.None, plain[3, 2].Style);

        GpuRenderer.DrawCursor(plain, 99, 2, visible: true);
        GpuRenderer.DrawCursor(plain, 3, -1, visible: true);
        Assert.Equal(CellStyle.None, plain[3, 2].Style);
    }
}
