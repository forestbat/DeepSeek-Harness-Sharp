using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Dsh.Gui.Views;

public sealed class MarkdownView : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string>(nameof(Text), "");

    private const int CollapseThresholdLines = 12;
    private const int CollapsedLineCount = 8;
    private const int MaxListLevel = 4;
    private const double BlockSpacing = 6;
    private const double InnerSpacing = 2;
    private const double ListIndentPerLevel = 16;
    private const double ListGap = 6;
    private const double BodyFontSize = 13.5;
    private const double CodeFontSize = 12.5;
    private const double CaptionFontSize = 11;
    private const double Heading1FontSize = 20;
    private const double Heading2FontSize = 17;
    private const double Heading3FontSize = 15;
    private const double CodeCornerRadius = 6;
    private const double CodePadding = 10;
    private const double QuoteBarWidth = 3;
    private const double QuotePadding = 10;
    private const string BulletMarker = "•";
    private const string DefaultCodeLanguage = "code";
    private const string MonoFontFallback = "Consolas, monospace";

    private static readonly IBrush TranslucentGray = new SolidColorBrush(Color.FromArgb(0x28, 0x80, 0x80, 0x80));
    private static readonly InlineDelimiter[] InlineDelimiters =
    [
        new("`", false, false, true),
        new("***", true, true, false),
        new("**", true, false, false),
        new("*", false, true, false),
    ];

    private readonly StackPanel _root = new() { Orientation = Orientation.Vertical, Spacing = BlockSpacing };
    private readonly Dictionary<int, bool> _expandedCodeBlocks = [];
    private string _renderedText = "";
    private double _fontScale = 1;
    private IBrush _primaryBrush = Brushes.Black;
    private IBrush _secondaryBrush = Brushes.Gray;
    private IBrush _codeBackground = TranslucentGray;
    private FontFamily _monoFont = FontFamily.Parse(MonoFontFallback);

    public MarkdownView()
    {
        Content = _root;
        ApplyTheme();
        ActualThemeVariantChanged += OnThemeVariantChanged;
        Rebuild(force: true);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            Rebuild(force: false);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Application.Current is { } app)
            app.ResourcesChanged += OnAppResourcesChanged;
        ApplyTheme();
        Rebuild(force: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Application.Current is { } app)
            app.ResourcesChanged -= OnAppResourcesChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /** 字号/主题资源变化后重建: 正文块的字号是构建期算出来的, 不会随 DynamicResource 自动刷新。 */
    private void OnAppResourcesChanged(object? sender, ResourcesChangedEventArgs e)
    {
        ApplyTheme();
        Rebuild(force: true);
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
        Rebuild(force: true);
    }

    private void ApplyTheme()
    {
        _primaryBrush = ResolveBrush("Brush.Text.Primary", IsDark ? Brushes.White : Brushes.Black);
        _secondaryBrush = ResolveBrush("Brush.Text.Secondary", IsDark ? Brushes.Silver : Brushes.DimGray);
        _codeBackground = ResolveBrush("Brush.Bg.Inset", TranslucentGray);
        _monoFont = LookupResource("Font.Mono") as FontFamily ?? FontFamily.Parse(MonoFontFallback);
        _fontScale = ResolveFontSize("FontSize.Body", BodyFontSize) / BodyFontSize;
    }

    private double BodySize => BodyFontSize * _fontScale;

    private double CodeSize => CodeFontSize * _fontScale;

    private double CaptionSize => CaptionFontSize * _fontScale;

    private double Scaled(double baseSize) => baseSize * _fontScale;

    private double ResolveFontSize(string key, double fallback) => LookupResource(key) is double value ? value : fallback;

    private IBrush ResolveBrush(string key, IBrush fallback) => LookupResource(key) as IBrush ?? fallback;

    private object? LookupResource(string key) => this.TryFindResource(key, out var value) ? value : null;

    private void Rebuild(bool force)
    {
        var text = Text;
        if (!force && string.Equals(text, _renderedText, StringComparison.Ordinal))
            return;
        _renderedText = text;
        _root.Children.Clear();
        if (!string.IsNullOrWhiteSpace(text))
            Render(text);
    }

    private void Render(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var index = 0;
        while (index < lines.Length)
            index = RenderNext(lines, index);
    }

    private int RenderNext(string[] lines, int index)
    {
        var line = lines[index];
        if (string.IsNullOrWhiteSpace(line))
            return index + 1;
        if (IsFence(line))
            return RenderCodeBlock(lines, index);
        if (ReadHeading(line) is { } heading)
        {
            AddHeading(heading);
            return index + 1;
        }
        if (ReadListMarker(line) is not null)
            return RenderList(lines, index);
        if (ReadQuoteContent(line) is not null)
            return RenderQuote(lines, index);
        return RenderParagraph(lines, index);
    }

    private void AddHeading(Heading heading)
    {
        var block = CreateTextBlock(heading.Content);
        block.FontSize = heading.Level switch
        {
            1 => Scaled(Heading1FontSize),
            2 => Scaled(Heading2FontSize),
            _ => Scaled(Heading3FontSize),
        };
        block.FontWeight = FontWeight.SemiBold;
        _root.Children.Add(block);
    }

    private int RenderParagraph(string[] lines, int index)
    {
        var content = new List<string>();
        while (index < lines.Length && IsParagraphLine(lines[index]))
        {
            content.Add(lines[index].Trim());
            index++;
        }
        _root.Children.Add(CreateTextBlock(string.Join('\n', content)));
        return index;
    }

    private int RenderList(string[] lines, int index)
    {
        var items = new StackPanel { Orientation = Orientation.Vertical, Spacing = InnerSpacing };
        while (index < lines.Length && ReadListMarker(lines[index]) is { } marker)
        {
            items.Children.Add(CreateListItem(lines[index][marker.ContentStart..], marker));
            index++;
        }
        _root.Children.Add(items);
        return index;
    }

    private Control CreateListItem(string content, ListMarker marker)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(marker.Level * ListIndentPerLevel, 0, 0, 0),
        };
        grid.Children.Add(new TextBlock
        {
            Text = marker.Marker,
            Foreground = _secondaryBrush,
            FontSize = BodySize,
            Margin = new Thickness(0, 0, ListGap, 0),
        });
        var text = CreateTextBlock(content);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private int RenderQuote(string[] lines, int index)
    {
        var content = new StackPanel { Orientation = Orientation.Vertical, Spacing = InnerSpacing };
        while (index < lines.Length && ReadQuoteContent(lines[index]) is { } quoted)
        {
            var block = CreateTextBlock(quoted);
            block.Foreground = _secondaryBrush;
            content.Children.Add(block);
            index++;
        }
        _root.Children.Add(new Border
        {
            BorderBrush = _secondaryBrush,
            BorderThickness = new Thickness(QuoteBarWidth, 0, 0, 0),
            Padding = new Thickness(QuotePadding, 0, 0, 0),
            Child = content,
        });
        return index;
    }

    private int RenderCodeBlock(string[] lines, int start)
    {
        var end = start + 1;
        while (end < lines.Length && !IsFence(lines[end]))
            end++;
        AddCodeBlock(lines, start, end);
        return end < lines.Length ? end + 1 : end;
    }

    private void AddCodeBlock(string[] lines, int start, int end)
    {
        var codeLines = lines[(start + 1)..end];
        var canToggle = codeLines.Length > CollapseThresholdLines;
        var isExpanded = !canToggle || (_expandedCodeBlocks.TryGetValue(start, out var stored) && stored);
        var visible = isExpanded ? codeLines : codeLines[..CollapsedLineCount];
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = InnerSpacing };
        panel.Children.Add(new TextBlock
        {
            Text = ReadLanguage(lines[start]),
            FontSize = CaptionSize,
            Foreground = _secondaryBrush,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join('\n', visible),
            FontFamily = _monoFont,
            FontSize = CodeSize,
            Foreground = _primaryBrush,
            TextWrapping = TextWrapping.Wrap,
        });
        if (canToggle)
            panel.Children.Add(CreateCodeToggle(start, codeLines.Length, isExpanded));
        _root.Children.Add(new Border
        {
            Background = _codeBackground,
            CornerRadius = new CornerRadius(CodeCornerRadius),
            Padding = new Thickness(CodePadding),
            Child = panel,
        });
    }

    private Button CreateCodeToggle(int blockStart, int totalLines, bool isExpanded)
    {
        var button = new Button
        {
            Content = isExpanded ? "折叠" : $"展开全部 {totalLines} 行",
            FontSize = CaptionSize,
            Foreground = _secondaryBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2, 8, 2),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => ToggleCodeBlock(blockStart, isExpanded);
        return button;
    }

    private void ToggleCodeBlock(int blockStart, bool isExpanded)
    {
        _expandedCodeBlocks[blockStart] = !isExpanded;
        Rebuild(force: true);
    }

    private TextBlock CreateTextBlock(string text) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = _primaryBrush,
        FontSize = BodySize,
        Inlines = BuildInlines(text),
    };

    private InlineCollection BuildInlines(string text)
    {
        var inlines = new InlineCollection();
        var plain = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            var (next, bold, italic, code, content) = ReadInlineSegment(text, index);
            if (content is null)
            {
                plain.Append(text[index]);
                index++;
                continue;
            }
            FlushPlain(inlines, plain);
            inlines.Add(CreateRun(content, bold, italic, code));
            index = next;
        }
        FlushPlain(inlines, plain);
        return inlines;
    }

    private static (int Next, bool Bold, bool Italic, bool Code, string? Content) ReadInlineSegment(string text, int index)
    {
        foreach (var delimiter in InlineDelimiters)
        {
            if (text.IndexOf(delimiter.Token, index, StringComparison.Ordinal) != index)
                continue;
            var end = text.IndexOf(delimiter.Token, index + delimiter.Token.Length, StringComparison.Ordinal);
            if (end < 0)
                continue;
            var content = text[(index + delimiter.Token.Length)..end];
            return (end + delimiter.Token.Length, delimiter.Bold, delimiter.Italic, delimiter.Code, content);
        }
        return (index, false, false, false, null);
    }

    private Run CreateRun(string content, bool bold, bool italic, bool code)
    {
        var run = new Run(content);
        if (bold)
            run.FontWeight = FontWeight.Bold;
        if (italic)
            run.FontStyle = FontStyle.Italic;
        if (code)
        {
            run.FontFamily = _monoFont;
            run.Background = _codeBackground;
        }
        return run;
    }

    private static void FlushPlain(InlineCollection inlines, StringBuilder plain)
    {
        if (plain.Length == 0)
            return;
        inlines.Add(new Run(plain.ToString()));
        plain.Clear();
    }

    private static bool IsFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    private static string ReadLanguage(string fenceLine)
    {
        var info = fenceLine.TrimStart()[3..].Trim();
        var separator = info.IndexOf(' ');
        var language = separator < 0 ? info : info[..separator];
        return language.Length == 0 ? DefaultCodeLanguage : language;
    }

    private static Heading? ReadHeading(string line)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
            hashes++;
        if (hashes is 0 or > 6)
            return null;
        if (hashes < line.Length && line[hashes] != ' ')
            return null;
        return new Heading(hashes, line[hashes..].Trim());
    }

    private static ListMarker? ReadListMarker(string line)
    {
        var spaces = 0;
        while (spaces < line.Length && line[spaces] == ' ')
            spaces++;
        var rest = line[spaces..];
        var level = Math.Min(spaces / 2, MaxListLevel);
        if (rest.StartsWith("- ", StringComparison.Ordinal) || rest.StartsWith("* ", StringComparison.Ordinal))
            return new ListMarker(BulletMarker, "- ".Length, level);
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            digits++;
        if (digits == 0 || digits + 1 >= rest.Length || rest[digits] != '.' || rest[digits + 1] != ' ')
            return null;
        return new ListMarker(rest[..(digits + 1)], digits + 2, level);
    }

    private static string? ReadQuoteContent(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '>')
            return null;
        return trimmed[1..].TrimStart();
    }

    private static bool IsParagraphLine(string line)
        => !string.IsNullOrWhiteSpace(line)
           && !IsFence(line)
           && ReadHeading(line) is null
           && ReadListMarker(line) is null
           && ReadQuoteContent(line) is null;

    private readonly record struct Heading(int Level, string Content);

    private readonly record struct ListMarker(string Marker, int ContentStart, int Level);

    private readonly record struct InlineDelimiter(string Token, bool Bold, bool Italic, bool Code);
}
