namespace Dsh.Gui.Services;

/**
 * 左闭右开的序号区间。
 *
 * 会话事件要么"已到手", 要么"还缺着": 后者用若干区间表示, 因为一次跳号可能连着出现好几段。
 * 只有"还缺"这一侧需要区间语义 —— 已到手的一侧由 SessionEventLog 的分段索引自己回答。
 */
public readonly record struct SeqRange(long Start, long End)
{
    public long Count => Math.Max(0, End - Start);

    public bool IsEmpty => Count == 0;

    /** 与另一段相接或重叠时合并, 否则返回 null。 */
    public SeqRange? Union(SeqRange other)
        => other.Start <= End && Start <= other.End
            ? new SeqRange(Math.Min(Start, other.Start), Math.Max(End, other.End))
            : null;
}
