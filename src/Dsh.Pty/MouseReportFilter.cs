namespace Dsh.Pty;

/**
 * 过滤宿主终端灌进来的鼠标上报。两种编码都要认:
 *   - 带前导 ESC: `ESC [ < … M/m`(SGR) 与 `ESC [ M` + 3 字节(X10);
 *   - **去掉 ESC**: ConPTY 的输入侧会吃掉报文前导 ESC, 客户端实际读到的是 `[ < … M/m` 或 `[ M` + 3 字节。
 * 为什么必须在客户端整条丢弃: 会话侧 ConPTY 同样会吃掉 ESC, 剩下的 `[<0;10;5M` 到了 TUI 就成了"键入文本",
 * 表现为 attach 画面/输入行里满屏 `^[[M` 乱码(已用端到端用例复现)。客户端只是字节管道, 无法跨读边界还原成可用的鼠标事件。
 * 不完整序列先留在 carry 等下一次读; 超过上限则原样放行, 以免误吞用户输入。
 */
internal sealed class MouseReportFilter
{
    /** carry 上限: 超出说明这不是鼠标报文(或已被截断), 原样放行而不是继续吞。 */
    private const int MaxCarry = 64;

    private const byte Escape = 0x1B;
    private readonly List<byte> _carry = [];

    public byte[] Filter(ReadOnlySpan<byte> input)
    {
        if (_carry.Count == 0 && input.IndexOf(Escape) < 0 && input.IndexOf((byte)'[') < 0)
            return input.ToArray();

        var buffer = new List<byte>(_carry.Count + input.Length);
        buffer.AddRange(_carry);
        buffer.AddRange(input.ToArray());
        _carry.Clear();

        var output = new List<byte>(buffer.Count);
        var index = 0;
        while (index < buffer.Count)
        {
            var consumed = MatchReport(buffer, index);
            if (consumed > 0)
            {
                index += consumed;
                continue;
            }

            if (CouldBeReport(buffer, index))
            {
                // 可能是被读边界切开的报文: 留住尾巴等下一次读, 其余按原样输出。
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

    /** 返回整条报文长度; 不是鼠标报文返回 0。 */
    private static int MatchReport(List<byte> buffer, int start)
    {
        var offset = 1;   // 跳过 ESC
        if (buffer[start] == Escape)
        {
            if (start + offset >= buffer.Count || buffer[start + offset] != (byte)'[')
                return 0;
        }
        else if (buffer[start] == (byte)'[')
        {
            offset = 0;   // ConPTY 吃掉了前导 ESC
        }
        else
        {
            return 0;
        }

        if (start + offset + 1 >= buffer.Count)
            return 0;
        var kind = buffer[start + offset + 1];

        // X10/legacy: [M 后紧跟 Cb Cx Cy
        if (kind == (byte)'M')
            return start + offset + 4 < buffer.Count ? offset + 5 : 0;

        // SGR: [< … (M|m)
        if (kind != (byte)'<')
            return 0;
        for (var index = start + offset + 2; index < buffer.Count; index++)
        {
            if (buffer[index] == (byte)'M' || buffer[index] == (byte)'m')
                return index - start + 1;
            if (buffer[index] == Escape)
                return 0;
        }

        return 0;
    }

    /** 当前位置是否"可能"是报文的开头(用于判断被读边界切开)。 */
    private static bool CouldBeReport(List<byte> buffer, int start)
    {
        var offset = 1;
        if (buffer[start] == Escape)
        {
            if (start + offset >= buffer.Count)
                return true;
            if (buffer[start + offset] != (byte)'[')
                return false;
        }
        else if (buffer[start] == (byte)'[')
        {
            offset = 0;
        }
        else
        {
            return false;
        }

        if (start + offset + 1 >= buffer.Count)
            return true;
        var kind = buffer[start + offset + 1];
        if (kind == (byte)'M')
            return start + offset + 4 >= buffer.Count;
        return kind == (byte)'<';
    }
}
