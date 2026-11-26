namespace Dsh.Tui;

/**
 * 转录正文的唯一一份存储: 可增长的 char 缓冲, 按 (起点, 长度) 取只读切片。
 *
 * 转录原来把同一段文字存了三份(StringBuilder 全文 + 逐行 string + 折行行 string), 会话越长这份重复越大。
 * 这里只留一份文本, 行与折行行都按偏移记账 —— 上一页补进来的内容只追加或整块前置, 已发布的偏移由调用方平移。
 */
internal sealed class TranscriptText
{
    private char[] _buffer = new char[1024];
    private int _length;

    public int Length => _length;

    /** 全文切片: 整块搬迁/导出时用, 只在帧间短暂存活。 */
    public ReadOnlySpan<char> Span => _buffer.AsSpan(0, _length);

    /** 取 [start, start + length) 的只读切片; 越界按实际长度夹取(调用方给的都是自己记过的区间)。 */
    public ReadOnlySpan<char> Slice(int start, int length)
    {
        if (start < 0 || start >= _length || length <= 0)
            return ReadOnlySpan<char>.Empty;
        return _buffer.AsSpan(start, Math.Min(length, _length - start));
    }

    public ReadOnlySpan<char> SliceToEnd(int start)
        => start < 0 || start > _length ? ReadOnlySpan<char>.Empty : _buffer.AsSpan(start, _length - start);

    public void Append(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return;
        EnsureCapacity(_length + text.Length);
        text.CopyTo(_buffer.AsSpan(_length));
        _length += text.Length;
    }

    /**
     * 把 text 整块插到最前面: 已有内容整体右移。
     * 单份缓冲的意义就在这里 —— 前置一页只要一次 memmove, 不必把整段正文重新物化;
     * 代价是所有已发布的偏移都要调用方平移。
     */
    public void Prepend(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return;
        EnsureCapacity(_length + text.Length);
        if (_length > 0)
            _buffer.AsSpan(0, _length).CopyTo(_buffer.AsSpan(text.Length));
        text.CopyTo(_buffer);
        _length += text.Length;
    }

    public void Clear() => _length = 0;

    /** 单个字符: 折行/宽度计算要逐字符走, 逐段取切片太碎。 */
    public char this[int index] => _buffer[index];

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
            return;
        var capacity = _buffer.Length;
        while (capacity < required)
            capacity *= 2;
        Array.Resize(ref _buffer, capacity);
    }
}
