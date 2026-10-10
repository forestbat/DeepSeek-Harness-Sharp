using System.Text;

namespace Dsh.Tui;

public enum CapturePlanAction
{
    Wait,
    Keys,
    Capture,
    Wheel,
    Resize,
    Dump,
}

/** 捕获计划的一步: 执行前等待 DelayMs, 再按键/拍帧(DelayMs 之前的时间即"等状态稳定")。 */
public sealed record CapturePlanStep(CapturePlanAction Action, string Value, int DelayMs);

/**
 * 方案 B 的脚本化按键计划: 让产品自己的 GPU 宿主按脚本注入按键并在指定时刻拍帧,
 * 与方案 A(测试侧解析字符屏再光栅化)对比, 用两图差异找隐藏 bug。
 * 文件格式(一行一条指令, `#` 注释, 空行忽略):
 *   wait 500
 *   keys /model\r
 *   capture 01-model-menu
 *   wheel 70 10 3
 *   resize 240 25
 *   dump frame-1   # 把该帧的 CellGrid 原文写到 <plan 同目录>/frame-1.grid.txt
 * 转义: \e=Esc \r=Enter \t=Tab \b=Backspace \n=LF \\=\ 与 \0xNN 形式(如 \x03=Ctrl+C)。
 */
public static class CapturePlan
{
    /** 拍帧前的默认稳定等待: 状态切换多在一帧内完成, 留一帧余量再读像素。 */
    public const int CaptureSettleMs = 400;

    public static IReadOnlyList<CapturePlanStep> Parse(string text)
    {
        var steps = new List<CapturePlanStep>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var space = line.IndexOf(' ');
            var directive = space < 0 ? line : line[..space];
            var argument = space < 0 ? "" : line[(space + 1)..].Trim();
            switch (directive)
            {
                case "wait" when int.TryParse(argument, out var delay) && delay >= 0:
                    steps.Add(new CapturePlanStep(CapturePlanAction.Wait, "", delay));
                    break;
                case "keys" when argument.Length > 0:
                    steps.Add(new CapturePlanStep(CapturePlanAction.Keys, Unescape(argument, index + 1), 0));
                    break;
                case "wheel" when TryParseWheel(argument, out var wheel):
                    steps.Add(new CapturePlanStep(CapturePlanAction.Wheel, wheel, 0));
                    break;
                case "resize" when TryParseSize(argument, out var size):
                    steps.Add(new CapturePlanStep(CapturePlanAction.Resize, size, 0));
                    break;
                case "dump" when argument.Length > 0:
                    steps.Add(new CapturePlanStep(CapturePlanAction.Dump, argument.Trim(), 0));
                    break;
                case "capture" when argument.Length > 0:
                    steps.Add(new CapturePlanStep(CapturePlanAction.Capture, argument, CaptureSettleMs));
                    break;
                default:
                    throw new FormatException($"capture plan 第 {index + 1} 行无法解析: \"{line}\"");
            }
        }

        return steps;
    }

    private static bool TryParseWheel(string argument, out string value)
    {
        value = "";
        var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var x)
            || !int.TryParse(parts[1], out var y)
            || !int.TryParse(parts[2], out var delta))
            return false;
        value = $"{x} {y} {delta}";
        return true;
    }

    private static bool TryParseSize(string argument, out string value)
    {
        value = "";
        var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var columns)
            || !int.TryParse(parts[1], out var rows)
            || columns <= 0
            || rows <= 0)
            return false;
        value = $"{columns} {rows}";
        return true;
    }

    private static string Unescape(string text, int lineNumber)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }
            if (index + 1 >= text.Length)
                throw new FormatException($"capture plan 第 {lineNumber} 行以孤立的反斜杠结尾");
            var next = text[++index];
            builder.Append(next switch
            {
                'e' => '\e',
                'r' => '\r',
                't' => '\t',
                'b' => '\b',
                'n' => '\n',
                '\\' => '\\',
                'x' when index + 2 < text.Length && TryHex(text.AsSpan(index + 1, 2), out var hex) => Advance(ref index, 2, hex),
                _ => throw new FormatException($"capture plan 第 {lineNumber} 行不认识的转义: \\{next}"),
            });
        }

        return builder.ToString();
    }

    private static char Advance(ref int index, int count, int value)
    {
        index += count;
        return (char)value;
    }

    private static bool TryHex(ReadOnlySpan<char> span, out int value)
        => int.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out value);
}

/** 计划的执行游标: 按时间推进, 每步只返回一次(纯逻辑, 便于用假时钟单测)。 */
public sealed class CapturePlanRunner(IReadOnlyList<CapturePlanStep> steps)
{
    private int _index;
    private long _deadline;

    public int Count => steps.Count;

    public bool IsFinished => _index >= steps.Count;

    /** 返回当前到期的一步并推进; 未到期或已结束时返回 null。 */
    public CapturePlanStep? Next(long nowMs)
    {
        if (IsFinished)
            return null;
        if (_deadline == 0)
            _deadline = nowMs + steps[_index].DelayMs;
        if (nowMs < _deadline)
            return null;
        var step = steps[_index++];
        _deadline = IsFinished ? 0 : nowMs + steps[_index].DelayMs;
        return step;
    }
}

/** 脚本字符 > 控制台按键(与真实终端按键等价, 供窗口宿主直接喂给 ChatWindow)。 */
public static class CaptureKeys
{
    public static ConsoleKeyInfo ToKeyInfo(char character) => character switch
    {
        '\r' or '\n' => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
        '\t' => new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false),
        '\e' => new ConsoleKeyInfo('\e', ConsoleKey.Escape, false, false, false),
        '\b' => new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
        '\u0003' => new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true),
        '\u0018' => new ConsoleKeyInfo('\u0018', ConsoleKey.X, false, false, true),
        '\u0010' => new ConsoleKeyInfo('\u0010', ConsoleKey.P, false, false, true),
        // 字母键带上真实 ConsoleKey: 否则 Ctrl+X 之后的和弦(如 w)认不出(Key=NoName)。
        >= 'a' and <= 'z' => Letter(character, char.ToUpperInvariant(character)),
        >= 'A' and <= 'Z' => Letter(character, character),
        _ => new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false),
    };

    private static ConsoleKeyInfo Letter(char character, char upper)
        => new(character, (ConsoleKey)((int)ConsoleKey.A + (upper - 'A')), false, false, false);
}
