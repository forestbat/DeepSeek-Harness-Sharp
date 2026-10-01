namespace Dsh.Tui;


public readonly record struct UiLayout(
    ConsoleRect Main,
    ConsoleRect RightPanel,
    ConsoleRect Input,
    ConsoleRect Status);

public static class LayoutEngine
{
    public const int MaximumRightPanelWidth = 40;
    public const int InputHeight = 2;

    /** 输入栏高度(含信息行)的可拖范围: 最小 2 行, 且至少给正文留 MinimumTranscriptRows 行。 */
    public const int MinimumInputHeight = 2;
    public const int MinimumTranscriptRows = 4;

    /** 侧栏(单窗格右栏)可拖范围: 最小 10 列(拖到最窄也留一条抓得回来的分割线), 正文至少保留 MinimumTranscriptWidth 列。 */
    public const int MinimumRightPanelWidth = 10;
    public const int MinimumTranscriptWidth = 12;

    /** 拖动分隔线时两侧窗格各自至少要留的格数。 */
    public const int MinimumPaneExtent = 4;

    private const double RightPanelRatio = 0.32;
    private const int StatusHeight = 1;
    private const int DividerRows = 2;

    /** 侧栏宽度夹取(0=隐藏); 放不下"最小正文+最小右栏"时隐藏, 免得把正文挤没。 */
    public static int ClampRightPanelWidth(int consoleWidth, int width)
    {
        var maximum = consoleWidth - 1 - MinimumTranscriptWidth;
        return maximum < MinimumRightPanelWidth ? 0 : Math.Clamp(width, MinimumRightPanelWidth, maximum);
    }

    /** 输入栏高度夹取: 至少 MinimumInputHeight 行, 且保留 MinimumTranscriptRows 行正文。 */
    public static int ClampInputHeight(int consoleHeight, int height)
    {
        var maximum = Math.Max(MinimumInputHeight, consoleHeight - StatusHeight - DividerRows - MinimumTranscriptRows);
        return Math.Clamp(height, MinimumInputHeight, maximum);
    }

    /**
     * 计算布局。`rightPanelWidth` / `inputHeight` 为用户拖出来的覆盖值, 传 null 用默认(比例/常量);
     * 两者都在这里统一夹取, 保证绘制、命中测试、拖动三处拿到的是同一个布局。
     */
    public static UiLayout Calculate(int consoleWidth, int consoleHeight, int? rightPanelWidth = null, int? inputHeight = null)
    {
        var width = Math.Max(1, consoleWidth);
        var height = Math.Max(1, consoleHeight);

        var panelWidth = rightPanelWidth is { } requested
            ? ClampRightPanelWidth(width, requested)
            : Math.Clamp(Math.Min(MaximumRightPanelWidth, (int)(width * RightPanelRatio)), 0, Math.Max(0, width - 2));

        var dividerColumn = panelWidth > 0 ? 1 : 0;
        var mainWidth = width - panelWidth - dividerColumn;

        var rows = inputHeight is { } requestedRows ? ClampInputHeight(height, requestedRows) : InputHeight;

        var bodyHeight = Math.Max(0, height - StatusHeight - rows - DividerRows);
        var inputY = Math.Min(bodyHeight + 1, Math.Max(0, height - rows));
        var statusY = Math.Min(inputY + rows + 1, Math.Max(0, height - StatusHeight));

        return new UiLayout(
            new ConsoleRect(0, 0, mainWidth, bodyHeight),
            new ConsoleRect(mainWidth + dividerColumn, 0, panelWidth, bodyHeight),
            new ConsoleRect(0, inputY, width, rows),
            new ConsoleRect(0, statusY, width, StatusHeight));
    }

    /** 对二叉分割树求值: 叶子得到矩形, 每次分割沿轴向预留 1 格画分隔线; 与 shell 布局独立组合。 */
    public static PaneLayout EvaluatePanes(PaneNode root, ConsoleRect area)
    {
        var panes = new List<PanePlacement>();
        var dividers = new List<SplitDivider>();
        EvaluatePanesInto(root, "", area, panes, dividers);
        return new PaneLayout(panes, dividers);
    }

    /** 拖动分隔线时把指针位置换算成合法 Ratio(两侧都至少保留 MinimumPaneExtent 格); 区域过小时维持原比例。 */
    public static double RatioForDrag(SplitDivider divider, int pointerAlong, double currentRatio)
    {
        var available = divider.SpanTotal - 1;
        if (available < (2 * MinimumPaneExtent) + 1)
            return currentRatio;
        var extent = Math.Clamp(pointerAlong - divider.SpanStart, MinimumPaneExtent, available - MinimumPaneExtent);
        return (double)extent / available;
    }

    private static void EvaluatePanesInto(PaneNode node, string path, ConsoleRect area, List<PanePlacement> panes, List<SplitDivider> dividers)
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
                dividers.Add(new SplitDivider(SplitOrientation.Vertical, path, area.X + firstWidth, area.Y, area.Height, area.X, area.Width));
                EvaluatePanesInto(split.First, path + "F", new ConsoleRect(area.X, area.Y, firstWidth, area.Height), panes, dividers);
                EvaluatePanesInto(split.Second, path + "S", new ConsoleRect(area.X + firstWidth + 1, area.Y, secondWidth, area.Height), panes, dividers);
                return;
            }
            case PaneSplit split:
            {
                var available = Math.Max(0, area.Height - 1);
                var firstHeight = FirstPaneExtent(available, split.Ratio);
                var secondHeight = available - firstHeight;
                dividers.Add(new SplitDivider(SplitOrientation.Horizontal, path, area.X, area.Y + firstHeight, area.Width, area.Y, area.Height));
                EvaluatePanesInto(split.First, path + "F", new ConsoleRect(area.X, area.Y, area.Width, firstHeight), panes, dividers);
                EvaluatePanesInto(split.Second, path + "S", new ConsoleRect(area.X, area.Y + firstHeight + 1, area.Width, secondHeight), panes, dividers);
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
