namespace Dsh.Interaction;

public enum DiffLineKind
{
    Context,
    Add,
    Delete,
}

public readonly record struct DiffLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text);

/** 一次文件改动的可折叠 diff 卡片; TUI/GUI 共同消费的展示模型。Source 记录提取来源(meta/patch/pairs)。 */
public sealed record DiffCard(string Title, string Source, IReadOnlyList<DiffLine> Lines)
{
    public int Added { get; } = Lines.Count(line => line.Kind == DiffLineKind.Add);

    public int Removed { get; } = Lines.Count(line => line.Kind == DiffLineKind.Delete);

    /**
     * 卡片正文的总行数; 大于 Lines.Count 表示 Lines 只是一份预览(投影缓存只留了前若干行)。
     * 0 是"没设过"的默认值, 此时 Lines 就是全部 —— 既有构造方式不必改动。
     */
    public int TotalLines
    {
        get => field == 0 ? Lines.Count : field;
        init;
    }
}

