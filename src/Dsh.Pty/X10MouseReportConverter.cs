using System.Text;

namespace Dsh.Pty;

/**
 * X10 鼠标报文 → SGR(客户端就地翻译)。
 *
 * 为什么不是丢掉: 终端形态下拖拽分隔线/滚轮/点击**只能**靠宿主终端上报。只支持 X10 的终端(Rider 内置终端不认 1006)
 * 只会发 X10, 而 ConPTY 在**会话侧**会把 X10 的前导连同 `[M` 一起吃掉, 只剩裸坐标字节变成键入文本(断点实测)。
 * 代理这里拿到的 X10 却是完整的(前导还在), SGR 又能穿过 ConPTY 变成真正的鼠标事件 —— 所以在客户端翻译,
 * 功能保住, 也不会再出现乱码。收到 SGR 的终端(Windows Terminal)本就发 SGR, 直接放行。
 *
 * 编码对照: X10 `ESC [ M` Cb Cx Cy → SGR `ESC [ < b ; x ; y M|m`(x/y 从 1 基转为 1 基; 移动位 = 32; 释放用 'm')。
 */
internal sealed class X10MouseReportConverter
{
    /** carry 上限: 超出说明这不是鼠标报文(或已被截断), 原样放行而不是继续吞。 */
    private const int MaxCarry = 64;

    private const byte Escape = 0x1B;
    private readonly List<byte> _carry = [];

    public byte[] Convert(ReadOnlySpan<byte> input)
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
            var consumed = MatchX10(buffer, index, out var sgr);
            if (consumed > 0)
            {
                output.AddRange(sgr);
                index += consumed;
                continue;
            }

            if (CouldBeX10(buffer, index))
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

    /** 返回整条 X10 报文长度与其 SGR 形式; 不是 X10(SGR/普通文本)返回 0。 */
    private static int MatchX10(List<byte> buffer, int start, out byte[] sgr)
    {
        sgr = [];
        var offset = 1;   // 跳过 ESC
        if (buffer[start] == Escape)
        {
            if (start + 1 >= buffer.Count || buffer[start + 1] != (byte)'[')
                return 0;
        }
        else if (buffer[start] == (byte)'[')
        {
            offset = 0;   // 终端也可能把前导去掉
        }
        else
        {
            return 0;
        }

        if (start + offset + 1 >= buffer.Count || buffer[start + offset + 1] != (byte)'M')
            return 0;
        if (start + offset + 4 >= buffer.Count)
            return 0;

        var cb = buffer[start + offset + 2] - 32;
        var cx = buffer[start + offset + 3] - 32;
        var cy = buffer[start + offset + 4] - 32;
        sgr = Encoding.ASCII.GetBytes(ToSgr(cb, cx, cy));
        return offset + 5;
    }

    private static string ToSgr(int cb, int cx, int cy)
    {
        var moved = (cb & 32) != 0;
        var button = (cb & 3) | (cb & 64) | (moved ? 32 : 0);
        var x = Math.Max(1, cx - 1);   // X10 坐标从 32+1 起算, SGR 用 1 基
        var y = Math.Max(1, cy - 1);
        // X10 的释放只报"无键按下"(低 2 位 = 3), 不区分哪个键: 按 SGR 惯例发左键释放
        return (cb & 64) == 0 && (cb & 3) == 3 && !moved
            ? $"\u001b[<0;{x};{y}m"
            : $"\u001b[<{button};{x};{y}M";
    }

    /** 当前位置是否"可能是一条 X10 报文的开头"(用于判断被读边界切开)。SGR 立刻放行。 */
    private static bool CouldBeX10(List<byte> buffer, int start)
    {
        var offset = 1;
        if (buffer[start] == Escape)
        {
            if (start + 1 >= buffer.Count)
                return true;
            if (buffer[start + 1] != (byte)'[')
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
        if (buffer[start + offset + 1] != (byte)'M')
            return false;
        return start + offset + 4 >= buffer.Count;
    }
}
