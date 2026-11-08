using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/** 时间轨道上的一条刻度: 会话里的第几条消息、它在这条轨道上的相对位置, 以及预览用的文字。 */
public sealed record TimelineMark(int Index, double Fraction, MessageViewModel Message);

/**
 * 会话右侧的时间轨道: 一条竖线加每条用户消息一个刻度。
 * 刻度按消息在整篇里的序号定位, 所以"竖线上的高度"就是"滚到那里能看到的东西";
 * 悬停高亮并回传索引(预览面板据此滚动), 点击回传索引(跳到那条消息)。
 */
public sealed class TimelineRail : Control
{
    private const double EdgePadding = 12;
    private const double TickHalfWidth = 3;
    private const double HoveredTickHalfWidth = 6;

    public static readonly StyledProperty<ListBox?> TranscriptProperty =
        AvaloniaProperty.Register<TimelineRail, ListBox?>(nameof(Transcript));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<TimelineRail, IBrush?>(nameof(LineBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<TimelineRail, IBrush?>(nameof(TickBrush), Brushes.White);

    private List<TimelineMark> _marks = [];
    private int _sourceCount = -1;
    private int _hovered;

    static TimelineRail() => AffectsRender<TimelineRail>(LineBrushProperty, TickBrushProperty);

    public TimelineRail() => _hovered = -1;

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

    /** 当前刻度: 消息源变化时重建。 */
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

    /** 消息源变了(新会话/回退截断)就重建刻度; 只在句柄里比个数, 不订阅集合事件。 */
    public void Rebuild()
    {
        if (_sourceCount == (Transcript?.ItemCount ?? -1))
            return;
        _sourceCount = Transcript?.ItemCount ?? -1;
        var items = Transcript?.Items;
        var total = _sourceCount;
        _marks = [];
        if (items is null || total <= 0)
            return;
        for (var index = 0; index < total; index++)
        {
            if (items[index] is not MessageViewModel { Kind: MessageKind.User } message)
                continue;
            _marks.Add(new TimelineMark(index, total > 1 ? (double)index / (total - 1) : 0, message));
        }
        if (_hovered >= _marks.Count)
            _hovered = -1;
    }

    /** 第 index 条刻度在轨道上的纵坐标。 */
    public double OffsetOf(int index)
    {
        var span = Math.Max(0, Bounds.Height - 2 * EdgePadding);
        return index >= 0 && index < _marks.Count ? EdgePadding + _marks[index].Fraction * span : double.NaN;
    }

    /** 指针位置落在哪条刻度上(容差按距离最近算, 不是命中哪个像素)。 */
    public int MarkAt(double y)
    {
        Rebuild();
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < _marks.Count; index++)
        {
            var distance = Math.Abs(OffsetOf(index) - y);
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = index;
        }
        return best;
    }

    public override void Render(DrawingContext context)
    {
        // 透明底让整条轨道可命中: 刻度只有几个像素宽, 空白处也要能悬停。
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

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHovered(MarkAt(e.GetPosition(this).Y));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHovered(-1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (MarkAt(e.GetPosition(this).Y) is var index and >= 0)
            Activated?.Invoke(index);
        e.Handled = true;
    }

    private void SetHovered(int index)
    {
        if (_hovered == index)
            return;
        _hovered = index;
        InvalidateVisual();
        HoverChanged?.Invoke(index);
    }
}
