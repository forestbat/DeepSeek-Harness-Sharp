namespace Dsh.Tui;

public sealed class TranscriptFold
{
    public required int Start { get; init; }

    public required int End { get; set; }

    public required string Label { get; set; }

    public required string Preview { get; set; }

    public bool Collapsed { get; set; } = true;

    /** 展开态当前滚动行(从 0 开始, 以源行计); 长内容只显示从它开始的 PageLines 行。 */
    public int Scroll { get; set; }

    /** 最近一次渲染算出的页数, 供折叠头点击时判断是否还有下一页。 */
    public int PageCount { get; set; } = 1;

    /** 最近一次渲染算出的内容行数(展开态可滚动范围的依据)。 */
    public int Lines { get; set; }

    /** 长内容是否按 PageLines 分页(只用于工具/结果/diff 这类可能很长的折叠)。 */
    public bool Paged { get; set; }
}