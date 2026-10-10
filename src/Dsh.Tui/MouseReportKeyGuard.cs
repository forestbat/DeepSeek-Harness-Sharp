namespace Dsh.Tui;

/**
 * 控制台输入记录侧的鼠标报文兜底(ConPTY)。
 * 为什么需要: ConPTY 的输入侧会吃掉鼠标报文的前导 ESC, 剩下的 `[<~M` / `[M`+3 字节会以**字符按键**的形式
 * 出现在控制台输入缓冲里; 不拦就原样打进输入行(表现为输入行里满屏 32+坐标 的乱码, 已多次复现)。
 * 判定用字节路径同一套 MouseReport 语法: 完整报文整条丢弃并还原成鼠标事件, 判定不是报文则把挂起的按键原样交出。
 */
internal sealed class MouseReportKeyGuard
{
    private readonly List<ConsoleKeyInfo> _keys = [];
    private readonly List<byte> _bytes = [];

    public bool HasPending => _keys.Count > 0;

    /** 只有这类按键才可能是鼠标报文的开头(ESC 或 '['), 普通按键可直接放行走快路径。 */
    public static bool MayStart(ConsoleKeyInfo key)
        => key.KeyChar is '\u001b' or '[' || key.Key == ConsoleKey.Escape;

    /** 挂起中的按键以原始顺序交出(判定不是报文 / 静默期到达)。 */
    public List<TerminalInputEvent> Flush()
    {
        var events = new List<TerminalInputEvent>(_keys.Count);
        foreach (var key in _keys)
            events.Add(TerminalInputEvent.FromKey(key));
        _keys.Clear();
        return events;
    }

    /** 吃进一个按键, 返回此刻应当交给 UI 的事件(挂起中返回空)。 */
    public List<TerminalInputEvent> Accept(ConsoleKeyInfo key)
    {
        _keys.Add(key);
        _bytes.Clear();
        foreach (var pending in _keys)
        {
            if (pending.KeyChar > 0xff)
                return Flush();
            _bytes.Add((byte)pending.KeyChar);
        }

        var match = MouseReport.Match(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bytes));
        if (match.Status == MouseReportStatus.Matched)
        {
            _keys.Clear();
            return [TerminalInputEvent.FromMouse(match.Mouse)];
        }

        if (match.Status == MouseReportStatus.NeedMore && _keys.Count <= MouseReport.MaxLength)
            return [];
        return Flush();
    }
}
