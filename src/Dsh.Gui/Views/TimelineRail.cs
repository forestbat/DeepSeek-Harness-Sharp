using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/** 时间轨道上的一条刻度: 会话里的第几条消息、它在这条轨道上的相对位置, 以及预览用的文字。 */
public sealed record TimelineMark(int Index, double Fraction, MessageViewModel Message);

/**
 * 会话右侧的时间轨道: 一条竖线加每条用户消息一个刻度。
 *
 * 刻度位置按**消息在内容里的真实高度**折算(已实体化的用实测位置, 未实体化的按平均行高顺推),
 * 所以竖线上的高度与右侧滚动条的位置是同一套坐标 —— 刻度指哪, 滚到那里就是那条消息。
 * 只有刻度本身是命中目标: 轨道空白既不预览也不跳转, 免得想去拖滚动条却"点到"远处的刻度。
 */
public sealed class TimelineRail : Control
{
    private const double EdgePadding = 12;
    private const double TickHalfWidth = 3;
    private const double HoveredTickHalfWidth = 6;

    /** 刻度上下的命中容差: 超出这个距离就不算"点在这条刻度上"。 */
    public const double TickReach = 7;

    /** 还没排过版时的兜底行高(只用于首帧估算, 不参与稳定状态)。 */
    private const double FallbackItemHeight = 40;

    public static readonly StyledProperty<ListBox?> TranscriptProperty =
        AvaloniaProperty.Register<TimelineRail, ListBox?>(nameof(Transcript));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<TimelineRail, IBrush?>(nameof(LineBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<TimelineRail, IBrush?>(nameof(TickBrush), Brushes.White);

    private List<TimelineMark> _marks = [];
    private int _hovered = -1;
    private MessageViewModel? _hoveredMessage;

    /** 已实测到的行高(按消息序号), 滚过的区域越多越准。 */
    private readonly Dictionary<int, double> _heights = [];
    private int _heightsForCount = -1;

    static TimelineRail() => AffectsRender<TimelineRail>(LineBrushProperty, TickBrushProperty);

    public ListBox? Transcript
    {
        get => GetValue(TranscriptProperty);
        set => SetValue(TranscriptProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? TickBrush
    {
        get => GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    /** 当前刻度; 每次取都按最新布局重算(内容长高/折叠/滚动都会改变刻度位置)。 */
    public IReadOnlyList<TimelineMark> Marks
    {
        get
        {
            Rebuild();
            return _marks;
        }
    }

    public event Action<int>? HoverChanged;

    public event Action<int>? Activated;

    /**
     * 重建刻度: 用"内容坐标里的顶端"除以内容总高。
     * 已实体化的项取实测位置, 其余按实测平均行高顺推 —— 与虚拟化面板自己的估算方式一致。
     */
    public void Rebuild()
    {
        var list = Transcript;
        var items = list?.Items;
        var total = list?.ItemCount ?? 0;
        _marks = [];
        if (list is null || items is null || total <= 0)
        {
            _hovered = -1;
            return;
        }
        var fractions = Positions(list, total);
        for (var index = 0; index < total; index++)
        {
            // 只认"真实时间线条目": 与 /timestamp 的候选同一套(用户自己发的消息, 带事件序号),
            // 合成出来的界面行、上下文注入行都不会变成刻度。
            if (items[index] is MessageViewModel { Kind: MessageKind.User, HasTimelineEntry: true } message)
                _marks.Add(new TimelineMark(index, fractions[index], message));
        }
        if (_hovered >= _marks.Count)
            _hovered = -1;
    }

    /**
     * 每一项在内容里的相对位置(0~1)。
     *
     * 累加"每项高度"而不是拿"实测顶端 + 平均行高"去拼: 虚拟化只给当前视口里的项实测位置,
     * 拿它当锚点会让未实体化的项全挤在锚点前面(刻度成簇), 也就是"刻度对不上消息"。
     * 这里把实测到的行高缓存下来(用户滚过的地方会越来越准), 没测过的用平均行高,
     * 再从第一项累加到末项 —— 单调、无跳变, 与滚动位置同一套比例。
     */
    private double[] Positions(ListBox list, int total)
    {
        var fractions = new double[total];
        if (list.Scroll is not ScrollViewer scroll || scroll.Extent.Height <= 0 || total == 0)
        {
            for (var index = 0; index < total; index++)
                fractions[index] = total > 1 ? (double)index / (total - 1) : 0;
            return fractions;
        }

        if (_heightsForCount != total)
        {
            _heights.Clear();
            _heightsForCount = total;
        }
        foreach (var item in list.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var index = list.IndexFromContainer(item);
            if (index >= 0 && index < total && item.Bounds.Height > 0)
                _heights[index] = item.Bounds.Height;
        }

        var average = _heights.Count > 0 ? _heights.Values.Average() : FallbackItemHeight;
        var heights = new double[total];
        var sum = 0.0;
        for (var index = 0; index < total; index++)
        {
            heights[index] = _heights.TryGetValue(index, out var measured) ? measured : average;
            sum += heights[index];
        }
        if (sum <= 0)
            return fractions;

        var top = 0.0;
        for (var index = 0; index < total; index++)
        {
            fractions[index] = Math.Clamp(top / sum, 0, 1);
            top += heights[index];
        }
        return fractions;
    }

    /** 第 index 条刻度在轨道上的纵坐标。 */
    public double OffsetOf(int index)
    {
        var span = Math.Max(0, Bounds.Height - 2 * EdgePadding);
        return index >= 0 && index < _marks.Count ? EdgePadding + _marks[index].Fraction * span : double.NaN;
    }

    /** 指针落在**哪条刻度上**: 超出 TickReach 的距离一律不算命中, 轨道的空白处什么都不做。 */
    public int MarkAt(double y)
    {
        Rebuild();
        var best = -1;
        var bestDistance = TickReach;
        for (var index = 0; index < _marks.Count; index++)
        {
            var distance = Math.Abs(OffsetOf(index) - y);
            if (distance > bestDistance)
                continue;
            bestDistance = distance;
            best = index;
        }
        return best;
    }

    public override void Render(DrawingContext context)
    {
        // 透明底只为了让指针进入轨道列时能收到事件; 命中判定仍然只认刻度。
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        Rebuild();
        var centerX = Bounds.Width / 2;
        var span = Bounds.Height - 2 * EdgePadding;
        if (span <= 0)
            return;
        if (LineBrush is { } line)
            context.DrawLine(new Pen(line, lineCap: PenLineCap.Round),
                new Point(centerX, EdgePadding), new Point(centerX, EdgePadding + span));
        if (TickBrush is not { } tick)
            return;
        for (var index = 0; index < _marks.Count; index++)
        {
            var y = Math.Round(OffsetOf(index)) + 0.5;
            var half = index == _hovered ? HoveredTickHalfWidth : TickHalfWidth;
            context.DrawLine(new Pen(tick, index == _hovered ? 2 : 1, lineCap: PenLineCap.Round),
                new Point(centerX - half, y), new Point(centerX + half, y));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (MarkAt(e.GetPosition(this).Y) is var index and >= 0)
            Activated?.Invoke(index);
        e.Handled = true;
    }

    /**
     * 悬停高亮。传入 -1 表示指针离开了刻度(但可能还在轨道列里): 高亮撤掉, 预览由调用方决定留还是收。
     * 同一条消息重复调用不触发事件, 免得指针在刻度附近抖动时预览反复重建。
     */
    public void SetHovered(int index)
    {
        var message = index >= 0 && index < _marks.Count ? _marks[index].Message : null;
        if (_hovered == index && ReferenceEquals(_hoveredMessage, message))
            return;
        _hovered = index;
        _hoveredMessage = message;
        InvalidateVisual();
        HoverChanged?.Invoke(index);
    }
}
