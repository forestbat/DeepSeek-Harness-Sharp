using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Dsh.Core;
using Dsh.Interaction;

namespace Dsh.Gui.Views;

/** 双列行号 + 绿增红删的 diff 卡片; 卡片内 chevron 控制本体展开态, 消息级折叠由 Preview/FoldLabel 承担。 */
public sealed class DiffView : UserControl
{
    public static readonly StyledProperty<DiffCard?> CardProperty =
        AvaloniaProperty.Register<DiffView, DiffCard?>(nameof(Card));

    public static readonly StyledProperty<bool> IsCardExpandedProperty =
        AvaloniaProperty.Register<DiffView, bool>(nameof(IsCardExpanded), defaultValue: true);

    private const double BodyFontSize = 12.5;
    private const double CaptionFontSize = 11;
    private const double LineSpacing = 0;
    private const double CardPadding = 10;
    private const double CardCornerRadius = 6;
    private const double NumberColumnPadding = 6;
    private const double CharWidthGuess = 7;
    private const string MonoFontFallback = "Consolas, monospace";

    private static readonly IBrush AddBackground = new SolidColorBrush(Color.FromArgb(0x2E, 0x2E, 0xA0, 0x43));
    private static readonly IBrush DeleteBackground = new SolidColorBrush(Color.FromArgb(0x2E, 0xF8, 0x51, 0x49));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly IBrush LineNumberBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80));
    private static readonly IBrush CardBackground = new SolidColorBrush(Color.FromArgb(0x28, 0x80, 0x80, 0x80));

    private readonly StackPanel _root = new() { Orientation = Orientation.Vertical, Spacing = LineSpacing };
    private readonly StackPanel _lines = new() { Orientation = Orientation.Vertical, Spacing = LineSpacing };
    private readonly TextBlock _chevron = new() { FontSize = CaptionFontSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontFamily _monoFont = FontFamily.Parse(MonoFontFallback);

    public DiffView()
    {
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
            Rebuild(change.NewValue as DiffCard);
        else if (change.Property == IsCardExpandedProperty)
            ApplyExpanded();
    }

    private void Rebuild(DiffCard? card)
    {
        _root.Children.Clear();
        _lines.Children.Clear();
        if (card is null)
            return;
        _root.Children.Add(BuildHeader(card));
        var numberWidth = NumberWidth(card.Lines);
        foreach (var line in card.Lines)
            _lines.Children.Add(BuildLine(line, numberWidth));
        _root.Children.Add(_lines);
        ApplyExpanded();
    }

    /** 卡片内 chevron: 折叠只隐藏 diff 行, 标题栏与计数始终可见。 */
    private void ApplyExpanded()
    {
        _chevron.Text = IsCardExpanded ? "▾" : "▸";
        _lines.IsVisible = IsCardExpanded;
    }

    private Control BuildHeader(DiffCard card)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        var toggle = new Button
        {
            Content = _chevron,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Click += (_, _) => IsCardExpanded = !IsCardExpanded;
        panel.Children.Add(toggle);
        panel.Children.Add(new TextBlock { Text = card.Title, FontSize = BodyFontSize, FontWeight = FontWeight.SemiBold });
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
            Background = line.Kind switch
            {
                DiffLineKind.Add => AddBackground,
                DiffLineKind.Delete => DeleteBackground,
                _ => Brushes.Transparent,
            },
        };
        grid.Children.Add(NumberBlock(line.OldLine, numberWidth, 0));
        grid.Children.Add(NumberBlock(line.NewLine, numberWidth, 1));
        var text = new TextBlock
        {
            Text = line.Text.Length == 0 ? " " : line.Text,
            FontFamily = _monoFont,
            FontSize = BodyFontSize,
            TextWrapping = TextWrapping.Wrap,
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

