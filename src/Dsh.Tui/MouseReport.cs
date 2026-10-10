namespace Dsh.Tui;

/** 一条鼠标报文的匹配结果。 */
internal enum MouseReportStatus
{
    /** 不是鼠标报文: 按普通输入处理。 */
    NotReport,

    /** 目前看起来像报文但还没到齐(被读边界切开, 或 ConPTY 分批投递)。 */
    NeedMore,

    /** 完整的一条报文。 */
    Matched,
}

internal readonly record struct MouseReportMatch(MouseReportStatus Status, TerminalMouseEvent Mouse, int Consumed);

/**
 * 鼠标报文语法(SGR `ESC [ < b;x;y M/m` 与 X10 `ESC [ M` + Cb Cx Cy)。
 * **前导 ESC 允许缺失**: ConPTY 的输入侧会吃掉报文前导 ESC, 客户端/会话实际读到的是 `[<~M` 或 `[M`+3 字节;
 * 只剩这些字节时仍必须整条识别并丢弃, 否则它们会变成"键入文本"污染输入行(该缺陷已在 Windows/WSL 反复复现)。
 * 完好的报文交给上层还原成鼠标事件, 不能只在客户端丢弃 -- 会话侧同样需要鼠标事件(拖动分隔线/滚动)。
 */
internal static class MouseReport
{
    private const byte Escape = 0x1B;

    /** 报文长度上限: 超过就不再当报文, 避免把用户输入长期挂在挂起态。 */
    public const int MaxLength = 24;

    public static MouseReportMatch Match(ReadOnlySpan<byte> text)
    {
        var index = 0;
        if (index < text.Length && text[index] == Escape)
            index++;   // 前导 ESC 可缺
        if (index >= text.Length)
            return NeedMore();
        if (text[index] != (byte)'[')
            return NotReport();
        index++;
        if (index >= text.Length)
            return NeedMore();

        var kind = text[index];
        if (kind == (byte)'M')
        {
            // X10: [M 后紧跟 Cb Cx Cy 三个可打印字节
            if (text.Length < index + 4)
                return NeedMore();
            for (var offset = 1; offset <= 3; offset++)
            {
                if (!IsReportByte(text[index + offset]))
                    return NotReport();
            }

            return Matched(ParseX10(text[index + 1], text[index + 2], text[index + 3]), index + 4);
        }

        if (kind != (byte)'<')
            return NotReport();

        // SGR: [< 参数...(M|m)
        for (var scan = index + 1; scan < text.Length; scan++)
        {
            var value = text[scan];
            if (value is (byte)'M' or (byte)'m')
            {
                // 参数含开头的 '<', ParseSgr 依赖它定位(与 CSI 的 parameters 语义一致)
                var parameters = System.Text.Encoding.ASCII.GetString(text[index..scan].ToArray());
                return Matched(ParseSgr(parameters, value == (byte)'M'), scan + 1);
            }

            if (value == Escape || !IsReportByte(value) || scan - index > MaxLength)
                return NotReport();
        }

        return text.Length - index > MaxLength ? NotReport() : NeedMore();
    }

    private static bool IsReportByte(byte value) => value >= 0x20;

    private static MouseReportMatch Matched(TerminalMouseEvent mouse, int consumed)
        => new(MouseReportStatus.Matched, mouse, consumed);

    private static MouseReportMatch NeedMore() => new(MouseReportStatus.NeedMore, default, 0);

    private static MouseReportMatch NotReport() => new(MouseReportStatus.NotReport, default, 0);

    private static TerminalMouseEvent ParseX10(byte cb, byte cx, byte cy)
    {
        var raw = cb - 32;
        var button = (raw & 64) != 0 ? 64 + (raw & 1) : raw & 3;
        var moved = (raw & 32) != 0;
        return new TerminalMouseEvent(Math.Max(0, cx - 33), Math.Max(0, cy - 33), button, (raw & 3) != 3, moved);
    }

    private static TerminalMouseEvent ParseSgr(string parameters, bool pressed)
    {
        var parts = parameters[1..].Split(';');
        var raw = parts.Length > 0 && int.TryParse(parts[0], out var value) ? value : 0;
        var x = parts.Length > 1 && int.TryParse(parts[1], out var px) ? px - 1 : 0;
        var y = parts.Length > 2 && int.TryParse(parts[2], out var py) ? py - 1 : 0;
        // 位 6 = 滚轮; 位 5 = 移动(1002 拖动/1003 全量跟踪), 按钮号在低 2 位(3 = 无键)
        var button = (raw & 64) != 0 ? 64 + (raw & 1) : raw & 3;
        var moved = (raw & 32) != 0;
        var actionPressed = (raw & 64) != 0 ? pressed : pressed && (raw & 3) != 3;
        return new TerminalMouseEvent(x, y, button, actionPressed, moved);
    }
}
