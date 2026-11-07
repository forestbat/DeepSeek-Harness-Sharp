using Dsh.Core;

namespace Dsh.Interaction;

/** diff 卡片的展示窗口: 每处改动前后各留几行上下文, 相距较远的改动之间用省略行分隔。 */
public static class DiffCardDisplay
{
    public const int ContextLines = 3;
    public const int PageLines = 20;

    public static IReadOnlyList<DiffLine> ForDisplay(DiffCard card, int contextLines = ContextLines)
        => ForDisplay(card.Lines, contextLines);

    public static IReadOnlyList<DiffLine> ForDisplay(IReadOnlyList<DiffLine> lines, int contextLines = ContextLines)
    {
        var windows = BuildWindows(lines, contextLines);
        var display = new List<DiffLine>();
        foreach (var (start, end) in windows)
        {
            if (display.Count > 0)
                display.Add(new DiffLine(DiffLineKind.Context, null, null, "…"));
            for (var index = start; index < end; index++)
                display.Add(lines[index]);
        }
        return display;
    }

    private static List<(int Start, int End)> BuildWindows(IReadOnlyList<DiffLine> lines, int contextLines)
    {
        var windows = new List<(int Start, int End)>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Kind == DiffLineKind.Context)
                continue;
            var start = Math.Max(0, index - contextLines);
            var end = Math.Min(lines.Count, index + contextLines + 1);
            if (windows.Count > 0 && start <= windows[^1].End)
                windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, end));
            else
                windows.Add((start, end));
        }
        if (windows.Count == 0 && lines.Count > 0)
            windows.Add((0, lines.Count));
        return windows;
    }
}
