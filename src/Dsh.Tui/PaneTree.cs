namespace Dsh.Tui;

/** 分割方向: Vertical 画竖线(左右并排), Horizontal 画横线(上下堆叠)。 */
public enum SplitOrientation
{
    Vertical,
    Horizontal,
}

public abstract record PaneNode;

public sealed record PaneLeaf(int PaneId) : PaneNode;

public sealed record PaneSplit(SplitOrientation Orientation, double Ratio, PaneNode First, PaneNode Second) : PaneNode;

public readonly record struct PanePlacement(int PaneId, ConsoleRect Rect);

/**
 * 分隔线: Path 从根起用 'F'/'S' 标识所属 PaneSplit(供拖动改比例时定位节点);
 * SpanStart/SpanTotal 是该分割所占区域的轴向起止(竖分割看 X/Width, 横分割看 Y/Height)。
 */
public readonly record struct SplitDivider(
    SplitOrientation Orientation,
    string Path,
    int X,
    int Y,
    int Length,
    int SpanStart,
    int SpanTotal);

public sealed record PaneLayout(IReadOnlyList<PanePlacement> Panes, IReadOnlyList<SplitDivider> Dividers)
{
    /** 命中测试纯函数: 返回 (x,y) 所在窗格 id; 分隔线或区域外返回 null。 */
    public int? HitTest(int x, int y)
    {
        foreach (var placement in Panes)
        {
            if (placement.Rect.Contains(x, y))
                return placement.PaneId;
        }
        return null;
    }
}

public enum FocusDirection
{
    Left,
    Right,
    Up,
    Down,
}

public static class PaneTree
{
    public const double DefaultRatio = 0.5;

    public static IReadOnlyList<int> PaneIds(PaneNode node)
    {
        var result = new List<int>();
        Collect(node, result);
        return result;
    }

    /** 把 target 叶子替换为 (target, newPane) 的二分; target 不存在时原样返回。 */
    public static PaneNode Split(PaneNode root, int targetPaneId, int newPaneId, SplitOrientation orientation)
        => root switch
        {
            PaneLeaf leaf when leaf.PaneId == targetPaneId =>
                new PaneSplit(orientation, DefaultRatio, leaf, new PaneLeaf(newPaneId)),
            PaneLeaf => root,
            PaneSplit split => split with
            {
                First = Split(split.First, targetPaneId, newPaneId, orientation),
                Second = Split(split.Second, targetPaneId, newPaneId, orientation),
            },
            _ => root,
        };

    /** 删除叶子并让父节点收缩为兄弟节点; 根被删除时返回 null。 */
    public static PaneNode? Remove(PaneNode root, int paneId)
        => root switch
        {
            PaneLeaf leaf => leaf.PaneId == paneId ? null : leaf,
            PaneSplit split => RemoveFromSplit(split, paneId),
            _ => root,
        };

    /** 按当前窗格矩形长宽比自动选分割方向: 宽则左右并排, 高则上下堆叠。 */
    public static SplitOrientation ChooseOrientation(ConsoleRect rect)
        => rect.Width >= rect.Height ? SplitOrientation.Vertical : SplitOrientation.Horizontal;

    /** 按路径(从根起 'F'/'S' 序列)定位分割节点并替换其 Ratio; 路径缺失时原样返回。 */
    public static PaneNode Resize(PaneNode root, string path, double ratio)
        => root switch
        {
            PaneSplit split when path.Length == 0 => split with { Ratio = ratio },
            PaneSplit split when path[0] == 'F' => split with { First = Resize(split.First, path[1..], ratio) },
            PaneSplit split when path[0] == 'S' => split with { Second = Resize(split.Second, path[1..], ratio) },
            _ => root,
        };

    /** 取路径对应分割节点的当前比例; 路径缺失时返回 false。 */
    public static bool TryGetRatio(PaneNode root, string path, out double ratio)
    {
        ratio = DefaultRatio;
        while (true)
        {
            if (root is not PaneSplit split)
                return false;
            if (path.Length == 0)
            {
                ratio = split.Ratio;
                return true;
            }
            root = path[0] switch
            {
                'F' => split.First,
                'S' => split.Second,
                _ => null!,
            };
            if (root is null)
                return false;
            path = path[1..];
        }
    }

    public static int? FindNeighbor(PaneLayout layout, int fromPaneId, FocusDirection direction)
    {
        var source = layout.Panes.FirstOrDefault(placement => placement.PaneId == fromPaneId);
        if (source.Rect.Width == 0 && source.Rect.Height == 0)
            return null;
        var sourceCenterX = (source.Rect.X * 2) + source.Rect.Width;
        var sourceCenterY = (source.Rect.Y * 2) + source.Rect.Height;
        var horizontal = direction is FocusDirection.Left or FocusDirection.Right;
        int? best = null;
        var bestPrimary = long.MaxValue;
        var bestSecondary = long.MaxValue;
        var bestPenalty = long.MaxValue;
        foreach (var candidate in layout.Panes)
        {
            if (candidate.PaneId == fromPaneId)
                continue;
            var centerX = (candidate.Rect.X * 2) + candidate.Rect.Width;
            var centerY = (candidate.Rect.Y * 2) + candidate.Rect.Height;
            var inDirection = direction switch
            {
                FocusDirection.Left => centerX < sourceCenterX,
                FocusDirection.Right => centerX > sourceCenterX,
                FocusDirection.Up => centerY < sourceCenterY,
                _ => centerY > sourceCenterY,
            };
            if (!inDirection)
                continue;
            var overlaps = horizontal
                ? candidate.Rect.Y < source.Rect.Bottom && candidate.Rect.Bottom > source.Rect.Y
                : candidate.Rect.X < source.Rect.Right && candidate.Rect.Right > source.Rect.X;
            var primary = direction switch
            {
                FocusDirection.Left => sourceCenterX - centerX,
                FocusDirection.Right => centerX - sourceCenterX,
                FocusDirection.Up => sourceCenterY - centerY,
                _ => centerY - sourceCenterY,
            };
            var secondary = horizontal ? Math.Abs(centerY - sourceCenterY) : Math.Abs(centerX - sourceCenterX);
            var penalty = overlaps ? 0L : long.MaxValue / 2;
            if (best is null
                || penalty < bestPenalty
                || (penalty == bestPenalty && primary < bestPrimary)
                || (penalty == bestPenalty && primary == bestPrimary && secondary < bestSecondary)
                || (penalty == bestPenalty && primary == bestPrimary && secondary == bestSecondary && candidate.PaneId < best.Value))
            {
                best = candidate.PaneId;
                bestPenalty = penalty;
                bestPrimary = primary;
                bestSecondary = secondary;
            }
        }
        return best;
    }

    /** 按摆放顺序循环到下一个窗格。 */
    public static int NextPane(PaneLayout layout, int fromPaneId)
    {
        if (layout.Panes.Count == 0)
            return fromPaneId;
        for (var index = 0; index < layout.Panes.Count; index++)
        {
            if (layout.Panes[index].PaneId == fromPaneId)
                return layout.Panes[(index + 1) % layout.Panes.Count].PaneId;
        }
        return layout.Panes[0].PaneId;
    }

    private static PaneNode? RemoveFromSplit(PaneSplit split, int paneId)
    {
        var first = Remove(split.First, paneId);
        var second = Remove(split.Second, paneId);
        if (first is null)
            return second;
        if (second is null)
            return first;
        return split with { First = first, Second = second };
    }

    private static void Collect(PaneNode node, List<int> result)
    {
        switch (node)
        {
            case PaneLeaf leaf:
                result.Add(leaf.PaneId);
                return;
            case PaneSplit split:
                Collect(split.First, result);
                Collect(split.Second, result);
                return;
        }
    }
}
