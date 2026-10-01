namespace Dsh.Pty;

/**
 * 丢掉"**开启**鼠标上报"的模式序列(CSI ? <mode> h)。
 * 鼠标报文进入 TUI 的源头就在这里: 常驻 TUI 写 `?1000h ?1002h ?1006h`, 终端形态下这些字节经隧道原样转发给
 * **宿主终端**, 宿主终端于是开始上报鼠标; 报文回流进会话后, Windows 的 ConPTY 把 `ESC[M` 吃掉, 只剩裸坐标字节
 * 以"字符按键"落到输入行(乱码), 同时终端文本选择被禁; 进程被强杀时终端还留在鼠标模式, 报表被 shell 回显。
 * 掐掉"开启"这一环, 宿主终端永远不上报, 下游就没有报文可漏。
 * `… l`(关闭)必须**原样放行**: 上一次运行可能在终端里留下了开启状态, 代理 attach 时正是靠它复位。
 */
internal sealed class MouseModeFilter
{
    /** carry 上限: 超出说明不是模式序列, 原样放行而不是继续吞。 */
    private const int MaxCarry = 32;

    private const byte Escape = 0x1B;
    private static readonly int[] MouseModes = [1000, 1002, 1003, 1005, 1006, 1015, 1016];
    private readonly List<byte> _carry = [];

    public byte[] Filter(ReadOnlySpan<byte> input)
    {
        if (_carry.Count == 0 && input.IndexOf(Escape) < 0)
            return input.ToArray();

        var buffer = new List<byte>(_carry.Count + input.Length);
        buffer.AddRange(_carry);
        buffer.AddRange(input.ToArray());
        _carry.Clear();

        var output = new List<byte>(buffer.Count);
        var index = 0;
        while (index < buffer.Count)
        {
            var consumed = MatchEnable(buffer, index);
            if (consumed > 0)
            {
                index += consumed;
                continue;
            }

            if (CouldBeEnable(buffer, index))
            {
                if (buffer.Count - index <= MaxCarry)
                    break;
            }

            output.Add(buffer[index]);
            index++;
        }

        if (index < buffer.Count)
        {
            var rest = buffer.GetRange(index, buffer.Count - index);
            if (rest.Count > MaxCarry)
                output.AddRange(rest);
            else
                _carry.AddRange(rest);
        }

        return output.ToArray();
    }

    /** 返回整条"开启鼠标上报"序列长度; 不是开启序列(含 `… l` 关闭序列)返回 0。 */
    private static int MatchEnable(List<byte> buffer, int start)
    {
        if (buffer[start] != Escape)
            return 0;
        if (start + 3 >= buffer.Count || buffer[start + 1] != (byte)'[' || buffer[start + 2] != (byte)'?')
            return 0;

        var index = start + 3;
        var value = 0;
        while (index < buffer.Count && buffer[index] is >= (byte)'0' and <= (byte)'9')
        {
            value = (value * 10) + (buffer[index] - (byte)'0');
            if (value > 100000)
                return 0;
            index++;
        }

        if (index >= buffer.Count || buffer[index] != (byte)'h')
            return 0;
        return Array.IndexOf(MouseModes, value) >= 0 ? index - start + 1 : 0;
    }

    /** 当前位置是否"还可能是一条开启鼠标上报的序列"(跨读边界等待用); 已经读全的一律返回 false, 交给 MatchEnable 决定去留。 */
    private static bool CouldBeEnable(List<byte> buffer, int start)
    {
        if (buffer[start] != Escape)
            return false;
        var index = start + 1;
        if (index >= buffer.Count)
            return true;
        if (buffer[index] != (byte)'[')
            return false;
        index++;
        if (index >= buffer.Count)
            return true;
        if (buffer[index] != (byte)'?')
            return false;

        // 只对鼠标模式那几个前缀继续等待, 其它 `?…h/l`(如 ?25l 光标)立即放行
        var prefix = "";
        for (index++; index < buffer.Count && buffer[index] is >= (byte)'0' and <= (byte)'9'; index++)
            prefix += (char)buffer[index];
        if (index >= buffer.Count)
            return MouseModes.Any(mode => mode.ToString().StartsWith(prefix, StringComparison.Ordinal));
        return false;
    }
}
