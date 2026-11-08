using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Dsh.Interaction;

namespace Dsh.Gui.Views;

/** 双列行号 + 绿增红删的 diff 卡片; 卡片内 chevron 控制本体展开态, 消息级折叠由 Preview/FoldLabel 承担。 */
public sealed class DiffView : UserControl
{
    public static readonly StyledProperty<DiffCard?> CardProperty =
        AvaloniaProperty.Register<DiffView, DiffCard?>(nameof(Card));

    public static readonly StyledProperty<bool> IsCardExpandedProperty =
        AvaloniaProperty.Register<DiffView, bool>(nameof(IsCardExpanded), defaultValue: false);

    /** 在卡片展开态改变前冒泡, 供消息列表固定卡片位置。 */
    public static readonly RoutedEvent<RoutedEventArgs> CardToggleRequestedEvent =
        RoutedEvent.Register<DiffView, RoutedEventArgs>("CardToggleRequested", RoutingStrategies.Bubble);

    private const double BodyFontSize = 12.5;
    private const double CaptionFontSize = 11;
    private const double LineSpacing = 0;
    private const double CardPadding = 10;
    private const double CardCornerRadius = 6;
    private const double NumberColumnPadding = 6;
    private const double CharWidthGuess = 7;
    private const string MonoFontFallback = "Consolas, monospace";

    /** 卡片内折叠按钮的名字: 卡片里还有滚动条按钮, 测试与自动化按名字定位它。 */
    public const string ChevronName = "DiffChevron";

    /** 展开后的行区域视口: 固定最多 VisibleLines 行高, 超出靠内层滚动条/滚轮查看。 */
    public ScrollViewer BodyViewport => _body;

    private const int VisibleLines = DiffCardDisplay.PageLines;

    /** 固定行高: 滚动窗口正好容纳 VisibleLines 行, 不随字形度量浮动。 */
    private const double LineHeight = 19;

    /** 渲染上限: 超大 diff 只渲染前若干行并补一行省略提示, 避免一次建出上万个 TextBlock。 */
    private const int MaxRenderedLines = 400;

    private static readonly IBrush AddBackground = new SolidColorBrush(Color.FromArgb(0x2E, 0x2E, 0xA0, 0x43));
    private static readonly IBrush DeleteBackground = new SolidColorBrush(Color.FromArgb(0x2E, 0xF8, 0x51, 0x49));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly IBrush LineNumberBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80));
    private static readonly IBrush CardBackground = new SolidColorBrush(Color.FromArgb(0x28, 0x80, 0x80, 0x80));

    private readonly StackPanel _root = new() { Orientation = Orientation.Vertical, Spacing = LineSpacing };
    private readonly StackPanel _lines = new() { Orientation = Orientation.Vertical, Spacing = LineSpacing };
    private readonly ScrollViewer _body = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxHeight = VisibleLines * LineHeight,
    };
    private TextBlock? _chevron;
    private readonly FontFamily _monoFont = FontFamily.Parse(MonoFontFallback);
    private DiffCard? _renderedCard;
    private DiffCard? _builtCard;
    private DiffCard? _pendingCard;
    private bool _rebuildPosted;

    public DiffView()
    {
        _body.Content = _lines;
        Content = new Border
        {
            Background = CardBackground,
            CornerRadius = new CornerRadius(CardCornerRadius),
            Padding = new Thickness(CardPadding),
            Child = _root,
        };
    }

    public DiffCard? Card
    {
        get => GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public bool IsCardExpanded
    {
        get => GetValue(IsCardExpandedProperty);
        set => SetValue(IsCardExpandedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CardProperty)
            QueueRebuild(change.NewValue as DiffCard);
        else if (change.Property == IsCardExpandedProperty)
            ApplyExpanded();
    }

    /**
     * 卡片内容必须在"展开"的同一轮布局里就位。
     * 早先只把 _body 设成可见、行内容留到下一次重建才填, 卡片高度因此分两段变化, 第二段(最多 380px)
     * 发生在点击之后很久, 列表面板据此重估坐标, 视口就跳。消息折叠没有这个两段式, 所以同样的滚动逻辑
     * 对消息没问题、对卡片就跳。
     */
    private void ApplyExpanded()
    {
        if (_chevron is not null)
            _chevron.Text = IsCardExpanded ? "▾" : "▸";
        if (IsCardExpanded && Card is { } card)
            BuildLines(card);
        _body.IsVisible = IsCardExpanded;
    }

    private void QueueRebuild(DiffCard? card)
    {
        _pendingCard = card;
        if (!_rebuildPosted && !ReferenceEquals(_renderedCard, card))
        {
            _root.Children.Clear();
            _lines.Children.Clear();
            _builtCard = null;
            if (card is not null)
            {
                _root.Children.Add(BuildHeader(card));
                _root.Children.Add(_body);
                ApplyExpanded();
            }
        }
        if (_rebuildPosted)
            return;
        _rebuildPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildPosted = false;
            var pending = _pendingCard;
            if (ReferenceEquals(_renderedCard, pending))
                return;
            Rebuild(pending);
        }, DispatcherPriority.Background);
    }

    private void Rebuild(DiffCard? card)
    {
        _renderedCard = card;
        _root.Children.Clear();
        _lines.Children.Clear();
        _builtCard = null;
        if (card is null)
            return;
        // 默认折叠: 只建标题栏, 行内容等用户展开时才建, 不会先铺满整段再收起。
        _root.Children.Add(BuildHeader(card));
        _root.Children.Add(_body);
        ApplyExpanded();
    }

    /** 展开态最多渲染 MaxRenderedLines 行, 其余靠内层滚动条查看; 视口固定 VisibleLines 行高。 */
    private void BuildLines(DiffCard card)
    {
        _lines.Children.Clear();
        var display = DiffCardDisplay.ForDisplay(card);
        var numberWidth = NumberWidth(display);
        var shown = Math.Min(display.Count, MaxRenderedLines);
        for (var index = 0; index < shown; index++)
            _lines.Children.Add(BuildLine(display[index], numberWidth));
        if (display.Count > shown)
        {
            _lines.Children.Add(new TextBlock
            {
                Text = $"… 其余 {display.Count - shown} 行请在卡片内滚动查看",
                FontSize = CaptionFontSize,
                Foreground = LineNumberBrush,
                Height = LineHeight,
            });
        }
        _builtCard = card;
    }

    private Control BuildHeader(DiffCard card)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        _chevron = new TextBlock { FontSize = CaptionFontSize, VerticalAlignment = VerticalAlignment.Center };
        var toggle = new Button
        {
            Name = ChevronName,
            Content = _chevron,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Click += (_, _) =>
        {
            RaiseEvent(new RoutedEventArgs(CardToggleRequestedEvent, this));
            IsCardExpanded = !IsCardExpanded;
        };
        panel.Children.Add(toggle);
        panel.Children.Add(new SelectableTextBlock { Text = card.Title, FontSize = BodyFontSize, FontWeight = FontWeight.SemiBold });
        if (card.Added > 0)
            panel.Children.Add(new TextBlock { Text = $"+{card.Added}", FontSize = CaptionFontSize, Foreground = AddedBrush, VerticalAlignment = VerticalAlignment.Center });
        if (card.Removed > 0)
            panel.Children.Add(new TextBlock { Text = $"-{card.Removed}", FontSize = CaptionFontSize, Foreground = RemovedBrush, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private Control BuildLine(DiffLine line, double numberWidth)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            Height = LineHeight,
            Background = line.Kind switch
            {
                DiffLineKind.Add => AddBackground,
                DiffLineKind.Delete => DeleteBackground,
                _ => Brushes.Transparent,
            },
        };
        grid.Children.Add(NumberBlock(line.OldLine, numberWidth, 0));
        grid.Children.Add(NumberBlock(line.NewLine, numberWidth, 1));
        var text = new SelectableTextBlock
        {
            Text = line.Text.Length == 0 ? " " : line.Text,
            FontFamily = _monoFont,
            FontSize = BodyFontSize,
            TextWrapping = TextWrapping.NoWrap,
        };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }

    private TextBlock NumberBlock(int? number, double width, int column)
    {
        var block = new TextBlock
        {
            Text = number?.ToString() ?? "",
            FontFamily = _monoFont,
            FontSize = CaptionFontSize,
            Foreground = LineNumberBrush,
            MinWidth = width,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, NumberColumnPadding, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(block, column);
        return block;
    }

    private static double NumberWidth(IReadOnlyList<DiffLine> lines)
    {
        var max = 0;
        foreach (var line in lines)
            max = Math.Max(max, Math.Max(line.OldLine ?? 0, line.NewLine ?? 0));
        var digits = 1;
        while (max >= 10)
        {
            max /= 10;
            digits++;
        }
        return digits * CharWidthGuess;
    }
}
