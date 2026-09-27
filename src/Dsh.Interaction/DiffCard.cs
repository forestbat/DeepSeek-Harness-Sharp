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
}

