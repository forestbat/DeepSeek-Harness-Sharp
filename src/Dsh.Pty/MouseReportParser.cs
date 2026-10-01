namespace Dsh.Pty;

/**
 * 从宿主终端输入字节流里摘出鼠标报文, 其余字节原样透传。
 *
 * 为什么必须由代理自己解析: ConPTY 的 VT 输入解析器只把 SGR(`ESC[<Cb;Cx;CyM/m`)翻成 MOUSE_EVENT 记录,
 * 对 legacy X10(`ESC[M`+3 字节)会把 `CSI M` 当普通 CSI 吃掉、再把 3 个载荷字节当字符泄漏出来 ——
 * 这正是 Rider/JediTerm 这类只发 X10 的终端在输入行留下 `@4;@5;` 乱码的原因。
 * 代理的控制台保留 VTI(原始字节透传)时报文是完整的, 解析在本地完成。详见项目记忆 conpty_mouse_x10_vs_sgr。
 *
 * 两种编码都认**带 ESC 与不带 ESC**的形态(ConPTY 有时吃掉前导); 单独的 ESC 立即透传, 不拖住 Esc 键。
 */
public sealed class MouseReportParser
{
    private const byte Escape = 0x1B;
    private const int MaxCarry = 64;
    private const int WheelDelta = 120;

    // Win32 MOUSE_EVENT 常量
    private const uint FromLeft1stButton = 0x0001;
    private const uint FromLeft2ndButton = 0x0004;
    private const uint RightmostButton = 0x0002;
    private const uint MouseMoved = 0x0001;
    private const uint MouseWheeled = 0x0004;

    private readonly List<byte> _carry = [];

    /** 追加一段输入, 返回应当继续透传的字节; 解析出的鼠标事件追加到 events。 */
    public byte[] Push(ReadOnlySpan<byte> input, List<PtyMouseEvent> events)
    {
        var buffer = Merge(input);
        var output = new List<byte>(buffer.Count);
        var index = 0;
        while (index < buffer.Count)
        {
            var current = buffer[index];
            if (current != Escape && current != (byte)'[')
            {
                output.Add(current);
                index++;
                continue;
            }

            var consumed = TryParse(buffer, index, events);
            if (consumed > 0)
            {
                index += consumed;
                continue;
            }

            if (IsPartialReport(buffer, index))
                break;

            output.Add(current);
            index++;
        }

        KeepCarry(buffer, index, output);
        return output.ToArray();
    }

    private List<byte> Merge(ReadOnlySpan<byte> input)
    {
        if (_carry.Count == 0)
            return [.. input];
        var buffer = new List<byte>(_carry.Count + input.Length);
        buffer.AddRange(_carry);
        buffer.AddRange(input.ToArray());
        _carry.Clear();
        return buffer;
    }

    private void KeepCarry(List<byte> buffer, int index, List<byte> output)
    {
        if (index >= buffer.Count)
            return;
        var rest = buffer.GetRange(index, buffer.Count - index);
        if (rest.Count > MaxCarry)
            output.AddRange(rest);
        else
            _carry.AddRange(rest);
    }

    /** 返回从 start 起消费掉的字节数; 不是鼠标报文返回 0。 */
    private static int TryParse(List<byte> buffer, int start, List<PtyMouseEvent> events)
    {
        var bracket = start;
        if (buffer[bracket] == Escape)
        {
            if (bracket + 1 >= buffer.Count || buffer[bracket + 1] != (byte)'[')
                return 0;
            bracket++;
        }

        if (bracket + 1 >= buffer.Count)
            return 0;
        var consumed = buffer[bracket + 1] switch
        {
            (byte)'M' => ParseX10(buffer, bracket, events),
            (byte)'<' => ParseSgr(buffer, bracket, events),
            _ => 0,
        };
        return consumed == 0 ? 0 : consumed + (bracket - start);
    }

    /** X10: `[ M` + Cb + Cx + Cy; 坐标与键值都带 32 偏移。bracket 指向 '['。 */
    private static int ParseX10(List<byte> buffer, int bracket, List<PtyMouseEvent> events)
    {
        if (bracket + 4 >= buffer.Count)
            return 0;
        var buttons = buffer[bracket + 2] - 32;
        var x = (short)(buffer[bracket + 3] - 32 - 1);
        var y = (short)(buffer[bracket + 4] - 32 - 1);
        events.Add(ToEvent((byte)buttons, x, y, release: (buttons & 3) == 3));
        return 5;
    }

    /** SGR: `[ < Cb ; Cx ; Cy` + (M|m); Cb 无偏移, 坐标 1-based, `m` 是释放。bracket 指向 '['。 */
    private static int ParseSgr(List<byte> buffer, int bracket, List<PtyMouseEvent> events)
    {
        var index = bracket + 2;
        if (!TryReadNumber(buffer, ref index, out var code) || !TryConsume(buffer, ref index, (byte)';'))
            return 0;
        if (!TryReadNumber(buffer, ref index, out var cx) || !TryConsume(buffer, ref index, (byte)';'))
            return 0;
        if (!TryReadNumber(buffer, ref index, out var cy) || index >= buffer.Count)
            return 0;
        if (buffer[index] != (byte)'M' && buffer[index] != (byte)'m')
            return 0;

        var release = buffer[index] == (byte)'m';
        events.Add(ToEvent((byte)code, (short)(cx - 1), (short)(cy - 1), release));
        return index - bracket + 1;
    }

    private static bool TryReadNumber(List<byte> buffer, ref int index, out int value)
    {
        value = 0;
        var digits = 0;
        while (index < buffer.Count && buffer[index] >= (byte)'0' && buffer[index] <= (byte)'9')
        {
            value = value * 10 + (buffer[index] - (byte)'0');
            index++;
            digits++;
        }

        return digits > 0;
    }

    private static bool TryConsume(List<byte> buffer, ref int index, byte expected)
    {
        if (index >= buffer.Count || buffer[index] != expected)
            return false;
        index++;
        return true;
    }

    /** 当前位置像是"还没收全的鼠标报文头"(等下一次读到再判)。单独的 ESC 不算, 立即透传。 */
    private static bool IsPartialReport(List<byte> buffer, int start)
    {
        var bracket = start;
        if (buffer[bracket] == Escape)
        {
            if (bracket + 1 >= buffer.Count || buffer[bracket + 1] != (byte)'[')
                return false;
            bracket++;
        }

        if (bracket + 1 >= buffer.Count)
            return true;
        return buffer[bracket + 1] switch
        {
            (byte)'M' => bracket + 4 >= buffer.Count,
            (byte)'<' => true,
            _ => false,
        };
    }

    /** buttons 已去掉 X10 的 32 偏移(SGR 本来就无偏移)。 */
    private static PtyMouseEvent ToEvent(byte buttons, short x, short y, bool release)
    {
        if ((buttons & 0x40) != 0)
        {
            // 滚轮: 高字是带符号的增量(+120 上 / -120 下)
            var delta = (buttons & 1) == 0 ? WheelDelta : -WheelDelta;
            return new PtyMouseEvent(x, y, (uint)(delta << 16), MouseWheeled);
        }

        var button = (uint)(buttons & 3);
        var state = release || button == 3
            ? 0u
            : button switch
            {
                0 => FromLeft1stButton,
                1 => FromLeft2ndButton,
                _ => RightmostButton,
            };
        var flags = (buttons & 0x20) != 0 ? MouseMoved : 0u;
        return new PtyMouseEvent(x, y, state, flags);
    }
}
