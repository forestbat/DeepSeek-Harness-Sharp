namespace Dsh.Tui;

/**
 * 输入区的软换行排版。
 *
 * 按显示宽度硬断行(不按词断): 英文长词会被切开, 这是输入框该有的行为, 不需要分词;
 * 宽字符(CJK)与代理对(emoji)不劈成两半; 换行符是强制换行。
 * 光标与视觉坐标的双向映射也在这里, 绘制与鼠标点击共用同一套口径。
 */
internal static class InputLayout
{
    /** 一个视觉行: 文本起点、长度(不含换行符)与显示宽度。 */
    public readonly record struct VisualRow(int Start, int Length, int Width);

    /** 视觉坐标: 第几行、该行第几列。 */
    public readonly record struct Position(int Row, int Column);

    public static IReadOnlyList<VisualRow> Wrap(string text, int width)
    {
        var rows = new List<VisualRow>();
        if (width <= 0)
        {
            rows.Add(new VisualRow(0, text.Length, TerminalTextWidth.Of(text)));
            return rows;
        }

        var start = 0;
        var used = 0;
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '\n')
            {
                rows.Add(new VisualRow(start, index - start, used));
                index++;
                start = index;
                used = 0;
                continue;
            }

            var unitLength = char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
            // 代理对是扩展平面字符, 终端按两格渲染; 逐 char 算会少算一格, 后续列整体左移。
            var unitWidth = unitLength == 2 ? 2 : TerminalTextWidth.Of(character);
            if (used + unitWidth > width && index > start)
            {
                rows.Add(new VisualRow(start, index - start, used));
                start = index;
                used = 0;
            }
            used += unitWidth;
            index += unitLength;
        }
        // 末尾的空行也要占一行: 光标停在换行符之后时, 用户看到的是新的一行。
        rows.Add(new VisualRow(start, text.Length - start, used));
        return rows;
    }

    public static Position Cursor(string text, int cursor, int width)
    {
        var rows = Wrap(text, width);
        var caret = Math.Clamp(cursor, 0, text.Length);
        for (var row = 0; row < rows.Count; row++)
        {
            var current = rows[row];
            if (caret >= current.Start && caret <= current.Start + current.Length)
                return new Position(row, TerminalTextWidth.Of(text.AsSpan(current.Start, caret - current.Start)));
        }
        var last = rows[^1];
        return new Position(rows.Count - 1, TerminalTextWidth.Of(text.AsSpan(last.Start, last.Length)));
    }

    /** 视觉坐标 → 文本偏移(鼠标点击落点); 落在行尾之后按行尾处理。 */
    public static int OffsetAt(string text, int row, int column, int width)
    {
        var rows = Wrap(text, width);
        if (row < 0)
            return 0;
        if (row >= rows.Count)
            return text.Length;
        var current = rows[row];
        var used = 0;
        var index = current.Start;
        while (index < current.Start + current.Length)
        {
            var unitLength = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
            var unitWidth = unitLength == 2 ? 2 : TerminalTextWidth.Of(text[index]);
            if (used + unitWidth > column)
                return index;
            used += unitWidth;
            index += unitLength;
        }
        return current.Start + current.Length;
    }
}
