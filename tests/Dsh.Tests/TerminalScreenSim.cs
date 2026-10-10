using System.Text;

namespace Dsh.Tests;

/**
 * 测试用的极简落屏模拟器: 只实现 AnsiRenderer 实际会发出的序列(CUP/ED/EL/CR/LF, SGR 忽略, 可打印字符),
 * 用来在测试里重建"终端上真正显示的画面"。
 * ambiguousWide=true 模拟把东亚宽度 Ambiguous 的字符按两格渲染的终端(简中控制台常见):
 * 用它比对渲染输出与 CellGrid, 就能发现"布局按一格、终端按两格"造成的行漂移与撕裂。
 * 与产品里的 Dsh.Pty.VtScreen(解析 shell 输出的 VT 解释器)用途不同, 故独立命名。
 */
public sealed class TerminalScreenSim(int width, int height, bool ambiguousWide = false)
{
    private readonly char[,] _screen = new char[height, width];
    private int _cx;
    private int _cy;
    private bool _pending;

    public void Feed(string sequence)
    {
        for (var i = 0; i < sequence.Length; i++)
        {
            var c = sequence[i];
            if (c == '\x1b')
            {
                i = HandleEscape(sequence, i);
                continue;
            }
            if (c == '\r')
            {
                _cx = 0;
                _pending = false;
                continue;
            }
            if (c == '\n')
            {
                LineFeed();
                continue;
            }
            if (c < 0x20)
                continue;
            Put(c);
        }
    }

    /** 终端重建的画面是否与网格一一对齐(字号差异按网格侧的落屏映射忽略)。 */
    public bool Matches(CellGrid grid) => Explain(grid) is null;

    /** 不匹配时给出逐行差异; 匹配返回 null。 */
    public string? Explain(CellGrid grid)
    {
        var lines = new List<string>();
        for (var y = 0; y < height; y++)
        {
            var gridRow = GridRow(grid, y);
            var screenRow = ScreenRow(y);
            if (string.Equals(gridRow, screenRow, StringComparison.Ordinal))
                continue;
            lines.Add($"y={y,2} grid=[{gridRow}]");
            lines.Add($"     screen=[{screenRow}]");
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private int CellWidth(char c)
    {
        if (TerminalTextWidth.IsWide(c))
            return 2;
        return ambiguousWide && IsAmbiguous(c) ? 2 : 1;
    }

    private static bool IsAmbiguous(char c) => c switch
    {
        >= '\u2010' and <= '\u22ff' => true,
        >= '\u2300' and <= '\u23ff' => true,
        >= '\u2460' and <= '\u24ff' => true,
        >= '\u2500' and <= '\u259f' => true,
        >= '\u25a0' and <= '\u25ff' => true,
        >= '\u2600' and <= '\u26ff' => true,
        >= '\u2700' and <= '\u27bf' => true,
        '\u00b7' or '\u00d7' or '\u00b0' or '\u00a7' or '\u00ae' => true,
        _ => false
    };

    private int HandleEscape(string s, int i)
    {
        var next = i + 1 < s.Length ? s[i + 1] : '\0';
        if (next == '[')
        {
            var j = i + 2;
            var parameters = new StringBuilder();
            while (j < s.Length && !(s[j] >= '@' && s[j] <= '~'))
            {
                parameters.Append(s[j]);
                j++;
            }
            if (j < s.Length)
                Csi(parameters.ToString(), s[j]);
            return j;
        }
        if (next == ']')
        {
            var j = i + 2;
            while (j < s.Length && s[j] != '\a' && !(s[j] == '\x1b' && j + 1 < s.Length && s[j + 1] == '\\'))
                j++;
            return s[j] == '\a' ? j : j + 1;
        }
        return i + 1;
    }

    private void Csi(string parameters, char final)
    {
        var parts = parameters.TrimStart('?').Split(';');
        int P(int index, int fallback) =>
            index < parts.Length && int.TryParse(parts[index], out var v) && v > 0 ? v : fallback;
        switch (final)
        {
            case 'H' or 'f':
                _cy = Math.Clamp(P(0, 1) - 1, 0, height - 1);
                _cx = Math.Clamp(P(1, 1) - 1, 0, width - 1);
                _pending = false;
                break;
            case 'G':
                _cx = Math.Clamp(P(0, 1) - 1, 0, width - 1);
                _pending = false;
                break;
            case 'A':
                _cy = Math.Max(0, _cy - P(0, 1));
                break;
            case 'B':
                _cy = Math.Min(height - 1, _cy + P(0, 1));
                break;
            case 'C':
                _cx = Math.Min(width - 1, _cx + P(0, 1));
                break;
            case 'D':
                _cx = Math.Max(0, _cx - P(0, 1));
                break;
            case 'J':
                if (P(0, 0) == 2)
                    Clear(0, 0, width, height);
                else
                    Clear(_cx, _cy, width - _cx, height - _cy);
                break;
            case 'K':
                Clear(_cx, _cy, width - _cx, 1);
                break;
        }
    }

    private void Clear(int x, int y, int w, int h)
    {
        for (var row = y; row < y + h && row < height; row++)
            for (var col = x; col < x + w && col < width; col++)
                if (row >= 0 && col >= 0)
                    _screen[row, col] = ' ';
    }

    private void LineFeed()
    {
        _pending = false;
        if (_cy < height - 1)
        {
            _cy++;
            return;
        }
        for (var row = 0; row < height - 1; row++)
            for (var col = 0; col < width; col++)
                _screen[row, col] = _screen[row + 1, col];
        for (var col = 0; col < width; col++)
            _screen[height - 1, col] = ' ';
    }

    private void Put(char c)
    {
        if (_pending)
        {
            _cx = 0;
            LineFeed();
            _pending = false;
        }
        var cellWidth = CellWidth(c);
        _screen[_cy, _cx] = c;
        if (cellWidth == 2 && _cx + 1 < width)
            _screen[_cy, _cx + 1] = '\u0001';
        _cx += cellWidth;
        if (_cx >= width)
        {
            _cx = width - 1;
            _pending = true;
        }
    }

    private string GridRow(CellGrid grid, int y)
    {
        var sb = new StringBuilder();
        for (var x = 0; x < width; x++)
        {
            var ch = grid[x, y].Character;
            if (ch == '\0' && x > 0 && TerminalTextWidth.IsWide(grid[x - 1, y].Character))
                continue;
            sb.Append(ch == '\0' ? ' ' : TerminalSafeGlyphs.AsciiSafe(ch));
        }
        return sb.ToString();
    }

    private string ScreenRow(int y)
    {
        var sb = new StringBuilder();
        for (var x = 0; x < width; x++)
        {
            var ch = _screen[y, x];
            if (ch == '\u0001')
                continue;
            sb.Append(ch == '\0' ? ' ' : ch);
        }
        return sb.ToString();
    }
}
