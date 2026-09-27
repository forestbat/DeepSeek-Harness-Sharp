using Dsh.Tui;

namespace Dsh.Tests;

/** 二叉分割树求值、命中测试、焦点导航: 纯几何, 不碰 UI。 */
public class PaneTreeTests
{
    [Fact]
    public void Single_Leaf_Fills_Area_Without_Dividers()
    {
        var layout = LayoutEngine.EvaluatePanes(new PaneLeaf(7), new ConsoleRect(3, 4, 20, 10));

        var placement = Assert.Single(layout.Panes);
        Assert.Equal(7, placement.PaneId);
        Assert.Equal(new ConsoleRect(3, 4, 20, 10), placement.Rect);
        Assert.Empty(layout.Dividers);
    }

    [Fact]
    public void Vertical_Split_Reserves_Divider_Column()
    {
        var root = new PaneSplit(SplitOrientation.Vertical, 0.5, new PaneLeaf(0), new PaneLeaf(1));

        var layout = LayoutEngine.EvaluatePanes(root, new ConsoleRect(0, 0, 100, 20));

        Assert.Equal(new ConsoleRect(0, 0, 50, 20), layout.Panes[0].Rect);
        Assert.Equal(new ConsoleRect(51, 0, 49, 20), layout.Panes[1].Rect);
        var divider = Assert.Single(layout.Dividers);
        Assert.Equal(SplitOrientation.Vertical, divider.Orientation);
        Assert.Equal(50, divider.X);
        Assert.Equal(20, divider.Length);
    }

    [Fact]
    public void Nested_Two_By_Two_Has_Four_Panes_And_Three_Dividers()
    {
        var layout = LayoutEngine.EvaluatePanes(TwoByTwo(), new ConsoleRect(0, 0, 100, 20));

        Assert.Equal(4, layout.Panes.Count);
        Assert.Equal(new ConsoleRect(0, 0, 50, 10), Find(layout, 0));
        Assert.Equal(new ConsoleRect(51, 0, 49, 10), Find(layout, 1));
        Assert.Equal(new ConsoleRect(0, 11, 50, 9), Find(layout, 2));
        Assert.Equal(new ConsoleRect(51, 11, 49, 9), Find(layout, 3));
        Assert.Equal(3, layout.Dividers.Count);
    }

    [Fact]
    public void Three_Columns_Come_From_Nested_Vertical_Splits()
    {
        var root = new PaneSplit(
            SplitOrientation.Vertical,
            0.5,
            new PaneLeaf(0),
            new PaneSplit(SplitOrientation.Vertical, 0.5, new PaneLeaf(1), new PaneLeaf(2)));

        var layout = LayoutEngine.EvaluatePanes(root, new ConsoleRect(0, 0, 100, 20));

        Assert.Equal(3, layout.Panes.Count);
        Assert.Equal(new ConsoleRect(0, 0, 50, 20), Find(layout, 0));
        Assert.Equal(new ConsoleRect(51, 0, 24, 20), Find(layout, 1));
        Assert.Equal(new ConsoleRect(76, 0, 24, 20), Find(layout, 2));
    }

    [Fact]
    public void Tiny_Area_Does_Not_Throw_And_Keeps_Leaves()
    {
        var layout = LayoutEngine.EvaluatePanes(TwoByTwo(), new ConsoleRect(0, 0, 1, 1));

        Assert.Equal(4, layout.Panes.Count);
    }

    [Fact]
    public void HitTest_Returns_Pane_And_Skips_Divider()
    {
        var layout = LayoutEngine.EvaluatePanes(TwoByTwo(), new ConsoleRect(0, 0, 100, 20));

        Assert.Equal(0, layout.HitTest(0, 3));
        Assert.Equal(1, layout.HitTest(70, 3));
        Assert.Equal(2, layout.HitTest(0, 15));
        Assert.Equal(3, layout.HitTest(70, 15));
        Assert.Null(layout.HitTest(50, 5));
        Assert.Null(layout.HitTest(-1, 5));
        Assert.Null(layout.HitTest(0, 999));
    }

    [Theory]
    [InlineData(0, FocusDirection.Right, 1)]
    [InlineData(0, FocusDirection.Down, 2)]
    [InlineData(1, FocusDirection.Left, 0)]
    [InlineData(1, FocusDirection.Down, 3)]
    [InlineData(2, FocusDirection.Right, 3)]
    [InlineData(2, FocusDirection.Up, 0)]
    [InlineData(3, FocusDirection.Left, 2)]
    [InlineData(3, FocusDirection.Up, 1)]
    public void FindNeighbor_On_Two_By_Two(int from, FocusDirection direction, int expected)
    {
        var layout = LayoutEngine.EvaluatePanes(TwoByTwo(), new ConsoleRect(0, 0, 100, 20));

        Assert.Equal(expected, PaneTree.FindNeighbor(layout, from, direction));
    }

    [Theory]
    [InlineData(0, FocusDirection.Left)]
    [InlineData(0, FocusDirection.Up)]
    [InlineData(3, FocusDirection.Right)]
    [InlineData(3, FocusDirection.Down)]
    public void FindNeighbor_Returns_Null_At_Edge(int from, FocusDirection direction)
    {
        var layout = LayoutEngine.EvaluatePanes(TwoByTwo(), new ConsoleRect(0, 0, 100, 20));

        Assert.Null(PaneTree.FindNeighbor(layout, from, direction));
    }

    [Fact]
    public void NextPane_Cycles_In_Placement_Order()
    {
        var layout = LayoutEngine.EvaluatePanes(TwoByTwo(), new ConsoleRect(0, 0, 100, 20));

        Assert.Equal(2, PaneTree.NextPane(layout, 0));
        Assert.Equal(1, PaneTree.NextPane(layout, 2));
        Assert.Equal(0, PaneTree.NextPane(layout, 3));
    }

    [Fact]
    public void Split_Replaces_Target_Leaf()
    {
        var tree = PaneTree.Split(new PaneLeaf(0), 0, 3, SplitOrientation.Horizontal);

        var layout = LayoutEngine.EvaluatePanes(tree, new ConsoleRect(0, 0, 10, 10));
        Assert.Equal([0, 3], PaneTree.PaneIds(tree));
        Assert.Equal(new ConsoleRect(0, 0, 10, 5), Find(layout, 0));
        Assert.Equal(new ConsoleRect(0, 6, 10, 4), Find(layout, 3));
    }

    [Fact]
    public void Split_Missing_Target_Is_Noop()
    {
        var tree = PaneTree.Split(new PaneLeaf(0), 9, 3, SplitOrientation.Vertical);

        Assert.Equal([0], PaneTree.PaneIds(tree));
    }

    [Fact]
    public void Remove_Collapses_Parent_To_Sibling()
    {
        var trimmed = PaneTree.Remove(TwoByTwo(), 2);

        Assert.NotNull(trimmed);
        Assert.Equal([0, 1, 3], PaneTree.PaneIds(trimmed));
        var layout = LayoutEngine.EvaluatePanes(trimmed, new ConsoleRect(0, 0, 100, 20));
        Assert.Equal(3, layout.Panes.Count);
        Assert.Equal(2, layout.Dividers.Count);
    }

    [Fact]
    public void Remove_Last_Leaf_Returns_Null()
    {
        Assert.Null(PaneTree.Remove(new PaneLeaf(0), 0));

        var tree = new PaneSplit(SplitOrientation.Vertical, 0.5, new PaneLeaf(0), new PaneLeaf(1));
        var remaining = PaneTree.Remove(tree, 0);
        Assert.NotNull(remaining);
        Assert.Null(PaneTree.Remove(remaining, 1));
    }

    [Fact]
    public void ChooseOrientation_Uses_Aspect_Ratio()
    {
        Assert.Equal(SplitOrientation.Vertical, PaneTree.ChooseOrientation(new ConsoleRect(0, 0, 80, 20)));
        Assert.Equal(SplitOrientation.Horizontal, PaneTree.ChooseOrientation(new ConsoleRect(0, 0, 20, 80)));
    }

    [Fact]
    public void Resize_Replaces_Ratio_At_Path_And_Keeps_Root_Ratio()
    {
        var root = PaneTree.Split(new PaneLeaf(0), 0, 1, SplitOrientation.Vertical);
        var nested = PaneTree.Split(root, 1, 2, SplitOrientation.Horizontal);

        var resized = PaneTree.Resize(nested, "S", 0.7);

        Assert.True(PaneTree.TryGetRatio(resized, "S", out var nestedRatio));
        Assert.Equal(0.7, nestedRatio, 3);
        Assert.True(PaneTree.TryGetRatio(resized, "", out var rootRatio));
        Assert.Equal(PaneTree.DefaultRatio, rootRatio, 3);
        Assert.Equal(nested, PaneTree.Resize(nested, "SF", 0.9));
        Assert.False(PaneTree.TryGetRatio(nested, "SS", out _));
    }

    [Fact]
    public void RatioForDrag_Clamps_Both_Orientations_And_Keeps_Ratio_When_Too_Small()
    {
        var vertical = new SplitDivider(SplitOrientation.Vertical, "", 40, 0, 33, 0, 79);
        Assert.Equal(50d / 78, LayoutEngine.RatioForDrag(vertical, 50, 0.5), 6);
        Assert.Equal(LayoutEngine.MinimumPaneExtent / 78d, LayoutEngine.RatioForDrag(vertical, 0, 0.5), 6);
        Assert.Equal((78 - LayoutEngine.MinimumPaneExtent) / 78d, LayoutEngine.RatioForDrag(vertical, 200, 0.5), 6);

        var horizontal = new SplitDivider(SplitOrientation.Horizontal, "", 0, 10, 79, 0, 33);
        Assert.Equal(15d / 32, LayoutEngine.RatioForDrag(horizontal, 15, 0.5), 6);

        var tiny = new SplitDivider(SplitOrientation.Horizontal, "", 0, 4, 79, 0, 9);
        Assert.Equal(0.5, LayoutEngine.RatioForDrag(tiny, 8, 0.5), 6);
    }

    private static PaneNode TwoByTwo()
        => new PaneSplit(
            SplitOrientation.Vertical,
            0.5,
            new PaneSplit(SplitOrientation.Horizontal, 0.5, new PaneLeaf(0), new PaneLeaf(2)),
            new PaneSplit(SplitOrientation.Horizontal, 0.5, new PaneLeaf(1), new PaneLeaf(3)));

    private static ConsoleRect Find(PaneLayout layout, int paneId)
        => layout.Panes.First(placement => placement.PaneId == paneId).Rect;
}
