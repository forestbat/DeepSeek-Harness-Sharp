using Dsh.Tui;

namespace Dsh.Tests;

public class LayoutEngineTests
{
    [Fact]
    public void Wide_Terminal_Uses_Fixed_280_Panel_Width()
    {
        var layout = LayoutEngine.Calculate(1000, 40);

        Assert.Equal(280, layout.RightPanel.Width);
        Assert.Equal(720, layout.Main.Width);
        Assert.Equal(38, layout.RightPanel.Height);
    }

    [Fact]
    public void Narrow_Terminal_Uses_Thirty_Percent_Panel_Width()
    {
        var layout = LayoutEngine.Calculate(100, 30);

        Assert.Equal(30, layout.RightPanel.Width);
        Assert.Equal(70, layout.Main.Width);
    }

    [Fact]
    public void Small_Terminal_Always_Reserves_Input_And_Status()
    {
        var layout = LayoutEngine.Calculate(20, 5);

        Assert.Equal(3, layout.Main.Height);
        Assert.Equal(3, layout.Input.Y);
        Assert.Equal(4, layout.Status.Y);
        Assert.Equal(20, layout.Input.Width);
        Assert.Equal(20, layout.Status.Width);
    }

    [Fact]
    public void Zero_Console_Size_Is_Clamped_To_One_Cell()
    {
        var layout = LayoutEngine.Calculate(0, 0);

        Assert.Equal(1, layout.Main.Width);
        Assert.Equal(0, layout.Main.Height);
        Assert.Equal(0, layout.RightPanel.Width);
        Assert.Equal(1, layout.Input.Width);
        Assert.Equal(1, layout.Status.Width);
    }

    [Fact]
    public void One_Column_Terminal_Keeps_Main_Area()
    {
        var layout = LayoutEngine.Calculate(1, 10);

        Assert.Equal(1, layout.Main.Width);
        Assert.Equal(0, layout.RightPanel.Width);
    }

    [Fact]
    public void Tiny_Terminal_Keeps_Input_And_Status_Inside_Bounds()
    {
        var layout = LayoutEngine.Calculate(80, 1);

        Assert.Equal(0, layout.Main.Height);
        Assert.InRange(layout.Input.Y, 0, 0);
        Assert.InRange(layout.Status.Y, 0, 0);
    }
}