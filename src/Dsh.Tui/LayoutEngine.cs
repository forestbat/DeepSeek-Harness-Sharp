namespace Dsh.Tui;

public readonly record struct ConsoleRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public readonly record struct UiLayout(
    ConsoleRect Main,
    ConsoleRect RightPanel,
    ConsoleRect Input,
    ConsoleRect Status);

public static class LayoutEngine
{
    public const int MaximumRightPanelWidth = 40;
    public const int InputHeight = 2;

    private const double RightPanelRatio = 0.32;
    private const int StatusHeight = 1;
    private const int DividerRows = 2;

    public static UiLayout Calculate(int consoleWidth, int consoleHeight)
    {
        var width = Math.Max(1, consoleWidth);
        var height = Math.Max(1, consoleHeight);

        var rightPanelWidth = Math.Min(MaximumRightPanelWidth, (int)(width * RightPanelRatio));
        rightPanelWidth = Math.Clamp(rightPanelWidth, 0, Math.Max(0, width - 2));

        var dividerColumn = rightPanelWidth > 0 ? 1 : 0;
        var mainWidth = width - rightPanelWidth - dividerColumn;

        var bodyHeight = Math.Max(0, height - StatusHeight - InputHeight - DividerRows);
        var inputY = Math.Min(bodyHeight + 1, Math.Max(0, height - InputHeight));
        var statusY = Math.Min(inputY + InputHeight + 1, Math.Max(0, height - StatusHeight));

        return new UiLayout(
            new ConsoleRect(0, 0, mainWidth, bodyHeight),
            new ConsoleRect(mainWidth + dividerColumn, 0, rightPanelWidth, bodyHeight),
            new ConsoleRect(0, inputY, width, InputHeight),
            new ConsoleRect(0, statusY, width, StatusHeight));
    }

    /** 对二叉分割树求值: 叶子得到矩形, 每次分割沿轴向预留 1 格画分隔线; 与 shell 布局独立组合。 */
    public static PaneLayout EvaluatePanes(PaneNode root, ConsoleRect area)
    {
        var panes = new List<PanePlacement>();
        var dividers = new List<SplitDivider>();
        EvaluatePanesInto(root, area, panes, dividers);
        return new PaneLayout(panes, dividers);
    }

    private static void EvaluatePanesInto(PaneNode node, ConsoleRect area, List<PanePlacement> panes, List<SplitDivider> dividers)
    {
        switch (node)
        {
            case PaneLeaf leaf:
                panes.Add(new PanePlacement(leaf.PaneId, area));
                return;
            case PaneSplit split when split.Orientation == SplitOrientation.Vertical:
            {
                var available = Math.Max(0, area.Width - 1);
                var firstWidth = FirstPaneExtent(available, split.Ratio);
                var secondWidth = available - firstWidth;
                dividers.Add(new SplitDivider(SplitOrientation.Vertical, area.X + firstWidth, area.Y, area.Height));
                EvaluatePanesInto(split.First, new ConsoleRect(area.X, area.Y, firstWidth, area.Height), panes, dividers);
                EvaluatePanesInto(split.Second, new ConsoleRect(area.X + firstWidth + 1, area.Y, secondWidth, area.Height), panes, dividers);
                return;
            }
            case PaneSplit split:
            {
                var available = Math.Max(0, area.Height - 1);
                var firstHeight = FirstPaneExtent(available, split.Ratio);
                var secondHeight = available - firstHeight;
                dividers.Add(new SplitDivider(SplitOrientation.Horizontal, area.X, area.Y + firstHeight, area.Width));
                EvaluatePanesInto(split.First, new ConsoleRect(area.X, area.Y, area.Width, firstHeight), panes, dividers);
                EvaluatePanesInto(split.Second, new ConsoleRect(area.X, area.Y + firstHeight + 1, area.Width, secondHeight), panes, dividers);
                return;
            }
            default:
                return;
        }
    }

    private static int FirstPaneExtent(int available, double ratio)
    {
        if (available <= 1)
            return available;
        var extent = (int)Math.Round(available * ratio, MidpointRounding.AwayFromZero);
        return Math.Clamp(extent, 1, available - 1);
    }
}
