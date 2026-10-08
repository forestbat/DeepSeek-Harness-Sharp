using System.Text;

namespace Dsh.Pty;

/**
 * VT 屏: 把 PTY 输出的字节流解释成单元格屏(含滚回缓冲), 供 shell 窗格渲染。
 * 覆盖真实 shell 需要的子集: C0 控制、CSI 光标/擦除/插入删除/滚动区/模式/SGR、OSC 透传丢弃、主/备用屏。
 */
public sealed class VtScreen
{
    private const int TabWidth = 8;
    private const int DefaultScrollbackLines = 2000;

    /** 渲染行缓冲的栈上限: 一行不超过这么多格就用栈缓冲, 否则退回堆数组。 */
    private const int StackRowCells = 256;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly List<Cell[]> _scrollback = [];
    private readonly StringBuilder _csi = new();
    private readonly StringBuilder _osc = new();

    private Cell[] _lines;
    private Cell[]? _altLines;
    private int _scrollTop;
    private int _scrollBottom;
    private int _savedX;
    private int _savedY;
    private int _savedScrollTop;
    private int _savedScrollBottom;
    private CellColor _penForeground = CellColor.Default;
    private CellColor _penBackground = CellColor.Default;
    private CellStyle _penStyle = CellStyle.None;
    private bool _wrapPending;
    private ParseState _state = ParseState.Ground;
    private char[] _decodeBuffer = new char[1024];

    public VtScreen(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        MaxScrollbackLines = DefaultScrollbackLines;
        _lines = NewLines(Width, Height);
        ResetScrollRegion();
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int MaxScrollbackLines { get; init; }

    public int CursorX { get; private set; }

    public int CursorY { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    /** DECCKM(`?1h`): vim/less 等全屏应用要求方向键发 `\eO` 前缀。 */
    public bool ApplicationCursorKeys { get; private set; }

    public int ScrollbackCount => _scrollback.Count;

    public ReadOnlySpan<Cell> Row(int y) => _lines.AsSpan(y * Width, Width);

    /** 滚回第 offset 行(0 = 屏幕顶行, 越大越旧); offset 超出滚回范围时返回空行。 */
    public ReadOnlySpan<Cell> ScrollbackRow(int offset)
    {
        var index = _scrollback.Count - 1 - offset;
        if (offset < 0 || index < 0 || index >= _scrollback.Count)
            return new Cell[Width];
        var line = _scrollback[index];
        return line.Length == Width ? line : line.AsSpan(0, Math.Min(line.Length, Width));
    }

    /** 追加 PTY 字节流; UTF-8 可能被读切分成多段, 由 Decoder 保留半成品序列。 */
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        var charCount = _decoder.GetCharCount(bytes, flush: false);
        if (charCount == 0)
            return;
        if (charCount > _decodeBuffer.Length)
            _decodeBuffer = new char[charCount];
        _decoder.GetChars(bytes, _decodeBuffer, flush: false);
        Feed(_decodeBuffer.AsSpan(0, charCount));
    }

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
            Consume(character);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
            return;
        var resized = NewLines(width, height);
        var rows = Math.Min(height, Height);
        for (var y = 0; y < rows; y++)
        {
            var copy = Math.Min(width, Width);
            _lines.AsSpan(y * Width, copy).CopyTo(resized.AsSpan(y * width, copy));
        }
        if (_altLines is not null)
        {
            var alt = NewLines(width, height);
            for (var y = 0; y < Math.Min(height, Height); y++)
            {
                var copy = Math.Min(width, Width);
                _altLines.AsSpan(y * Width, copy).CopyTo(alt.AsSpan(y * width, copy));
            }
            _altLines = alt;
        }
        _lines = resized;
        Width = width;
        Height = height;
        CursorX = Math.Clamp(CursorX, 0, Width - 1);
        CursorY = Math.Clamp(CursorY, 0, Height - 1);
        ResetScrollRegion();
    }

    /** 把当前屏(可带滚回偏移)写入目标矩形; 越界部分裁掉。 */
    public void Render(CellGrid grid, ConsoleRect rect, int scrollOffset = 0)
    {
        var rows = Math.Min(rect.Height, Height);
        // stackalloc 必须在循环外: 循环内的栈分配要到方法返回才回收, 满屏渲染会按行数线性吃栈(CA2014)
        Span<Cell> stackRow = stackalloc Cell[StackRowCells];
        for (var y = 0; y < rows; y++)
        {
            var source = y - scrollOffset;
            var line = source >= 0
                ? _lines.AsSpan(source * Width, Width)
                : ScrollbackRow(-source - 1);
            var width = Math.Min(rect.Width, line.Length);
            var buffer = width <= StackRowCells ? stackRow[..width] : new Cell[width];
            for (var x = 0; x < width; x++)
                buffer[x] = line[x];
            grid.SetRow(rect.Y + y, buffer);
        }
    }

    private static Cell[] NewLines(int width, int height)
    {
        var lines = new Cell[width * height];
        lines.AsSpan().Fill(new Cell(' '));
        return lines;
    }

    private void ResetScrollRegion()
    {
        _scrollTop = 0;
        _scrollBottom = Height - 1;
    }

    private void Consume(char character)
    {
        switch (_state)
        {
            case ParseState.Ground:
                Ground(character);
                return;
            case ParseState.Escape:
                Escape(character);
                return;
            case ParseState.Csi:
                Csi(character);
                return;
            case ParseState.Osc:
                Osc(character);
                return;
            case ParseState.OscEscape:
                _state = character == '\\' ? ParseState.Ground : ParseState.Osc;
                if (_state == ParseState.Osc)
                    _osc.Append(character);
                return;
            case ParseState.EscapeCharset:
                _state = ParseState.Ground;
                return;
        }
    }

    private void Ground(char character)
    {
        switch (character)
        {
            case '\u001b':
                _state = ParseState.Escape;
                return;
            case '\r':
                CursorX = 0;
                _wrapPending = false;
                return;
            case '\n' or '\v' or '\f':
                LineFeed();
                return;
            case '\b':
                CursorX = Math.Max(0, CursorX - 1);
                _wrapPending = false;
                return;
            case '\t':
                CursorX = Math.Min(Width - 1, (CursorX / TabWidth + 1) * TabWidth);
                _wrapPending = false;
                return;
            case '\a':
                return;
            default:
                if (character >= ' ')
                    Put(character);
                return;
        }
    }

    private void Escape(char character)
    {
        _state = ParseState.Ground;
        switch (character)
        {
            case '[':
                _csi.Clear();
                _state = ParseState.Csi;
                return;
            case ']':
                _osc.Clear();
                _state = ParseState.Osc;
                return;
            case 'M':
                ReverseIndex();
                return;
            case 'D':
                LineFeed();
                return;
            case 'E':
                CursorX = 0;
                LineFeed();
                return;
            case '7':
                SaveCursor();
                return;
            case '8':
                RestoreCursor();
                return;
            case '(' or ')' or '*' or '+' or '-' or '.' or '/':
                _state = ParseState.EscapeCharset;
                return;
            default:
                return;
        }
    }

    private void Csi(char character)
    {
        if (character is >= '0' and <= '9' or ';' or ':' or '?' or '<' or '=' or '>' or '!' or '"' or '$' or ' ')
        {
            _csi.Append(character);
            return;
        }
        _state = ParseState.Ground;
        var parameters = _csi.ToString();
        _csi.Clear();
        DispatchCsi(parameters, character);
    }

    private void Osc(char character)
    {
        switch (character)
        {
            case '\a':
                _state = ParseState.Ground;
                return;
            case '\u001b':
                _state = ParseState.OscEscape;
                return;
            default:
                _osc.Append(character);
                return;
        }
    }

    private void DispatchCsi(string parameters, char final)
    {
        var privateMode = parameters.StartsWith('?');
        var body = privateMode ? parameters[1..] : parameters;
        switch (final)
        {
            case 'A':
                CursorY = Math.Max(_scrollTop, CursorY - Count(body, 1));
                _wrapPending = false;
                return;
            case 'B':
                CursorY = Math.Min(_scrollBottom, CursorY + Count(body, 1));
                _wrapPending = false;
                return;
            case 'C':
                CursorX = Math.Min(Width - 1, CursorX + Count(body, 1));
                _wrapPending = false;
                return;
            case 'D':
                CursorX = Math.Max(0, CursorX - Count(body, 1));
                _wrapPending = false;
                return;
            case 'E':
                CursorY = Math.Min(_scrollBottom, CursorY + Count(body, 1));
                CursorX = 0;
                _wrapPending = false;
                return;
            case 'F':
                CursorY = Math.Max(_scrollTop, CursorY - Count(body, 1));
                CursorX = 0;
                _wrapPending = false;
                return;
            case 'G' or '`':
                CursorX = Math.Clamp(Count(body, 1) - 1, 0, Width - 1);
                _wrapPending = false;
                return;
            case 'd':
                CursorY = Math.Clamp(Count(body, 1) - 1, 0, Height - 1);
                _wrapPending = false;
                return;
            case 'H' or 'f':
                MoveCursor(body);
                return;
            case 'J':
                EraseDisplay(Count(body, 0));
                return;
            case 'K':
                EraseLine(Count(body, 0));
                return;
            case 'L':
                InsertLines(Count(body, 1));
                return;
            case 'M':
                DeleteLines(Count(body, 1));
                return;
            case 'P':
                DeleteCharacters(Count(body, 1));
                return;
            case 'X':
                EraseCharacters(Count(body, 1));
                return;
            case '@':
                InsertCharacters(Count(body, 1));
                return;
            case 'S':
                for (var index = 0; index < Count(body, 1); index++)
                    ScrollRegionUp();
                return;
            case 'T':
                for (var index = 0; index < Count(body, 1); index++)
                    ScrollRegionDown();
                return;
            case 'r':
                SetScrollRegion(body);
                return;
            case 'm':
                SelectGraphicRendition(body);
                return;
            case 'h':
                SetMode(privateMode, body, enabled: true);
                return;
            case 'l':
                SetMode(privateMode, body, enabled: false);
                return;
            case 's':
                SaveCursor();
                return;
            case 'u':
                RestoreCursor();
                return;
            default:
                return;
        }
    }

    private void MoveCursor(string body)
    {
        var parts = body.Split(';');
        var row = parts.Length > 0 && int.TryParse(parts[0], out var parsedRow) && parsedRow > 0 ? parsedRow : 1;
        var column = parts.Length > 1 && int.TryParse(parts[1], out var parsedColumn) && parsedColumn > 0 ? parsedColumn : 1;
        CursorY = Math.Clamp(row - 1, 0, Height - 1);
        CursorX = Math.Clamp(column - 1, 0, Width - 1);
        _wrapPending = false;
    }

    private void SetScrollRegion(string body)
    {
        var parts = body.Split(';');
        var top = parts.Length > 0 && int.TryParse(parts[0], out var parsedTop) && parsedTop > 0 ? parsedTop : 1;
        var bottom = parts.Length > 1 && int.TryParse(parts[1], out var parsedBottom) && parsedBottom > 0 ? parsedBottom : Height;
        if (bottom <= top)
            return;
        _scrollTop = Math.Clamp(top - 1, 0, Height - 1);
        _scrollBottom = Math.Clamp(bottom - 1, 0, Height - 1);
        CursorX = 0;
        CursorY = _scrollTop;
        _wrapPending = false;
    }

    private void SetMode(bool privateMode, string body, bool enabled)
    {
        if (!privateMode)
            return;
        foreach (var mode in body.Split(';'))
        {
            switch (mode)
            {
                case "25":
                    CursorVisible = enabled;
                    break;
                case "1":
                    ApplicationCursorKeys = enabled;
                    break;
                case "1049" or "47" or "1047" when enabled:
                    EnterAltScreen();
                    break;
                case "1049" or "47" or "1047":
                    LeaveAltScreen();
                    break;
                default:
                    break;
            }
        }
    }

    private void EnterAltScreen()
    {
        if (_altLines is not null)
            return;
        _altLines = _lines;
        _lines = NewLines(Width, Height);
        SaveCursor();
        CursorX = 0;
        CursorY = 0;
        ResetScrollRegion();
    }

    private void LeaveAltScreen()
    {
        if (_altLines is null)
            return;
        _lines = _altLines;
        _altLines = null;
        RestoreCursor();
        ResetScrollRegion();
    }

    private void SaveCursor()
    {
        _savedX = CursorX;
        _savedY = CursorY;
        _savedScrollTop = _scrollTop;
        _savedScrollBottom = _scrollBottom;
    }

    private void RestoreCursor()
    {
        CursorX = Math.Clamp(_savedX, 0, Width - 1);
        CursorY = Math.Clamp(_savedY, 0, Height - 1);
        _scrollTop = Math.Clamp(_savedScrollTop, 0, Height - 1);
        _scrollBottom = Math.Clamp(_savedScrollBottom, _scrollTop, Height - 1);
        _wrapPending = false;
    }

    private void SelectGraphicRendition(string body)
    {
        if (body.Length == 0)
            body = "0";
        var parts = body.Split(';');
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out var code))
                continue;
            switch (code)
            {
                case 0:
                    _penForeground = AnsiColor.Default;
                    _penBackground = AnsiColor.Default;
                    _penStyle = CellStyle.None;
                    break;
                case 1:
                    _penStyle |= CellStyle.Bold;
                    break;
                case 2:
                    _penStyle |= CellStyle.Dim;
                    break;
                case 7:
                    _penStyle |= CellStyle.Reverse;
                    break;
                case 22:
                    _penStyle &= ~(CellStyle.Bold | CellStyle.Dim);
                    break;
                case 27:
                    _penStyle &= ~CellStyle.Reverse;
                    break;
                case >= 30 and <= 37:
                    _penForeground = (AnsiColor)(code - 30 + 1);
                    break;
                case 39:
                    _penForeground = AnsiColor.Default;
                    break;
                case >= 40 and <= 47:
                    _penBackground = (AnsiColor)(code - 40 + 1);
                    break;
                case 49:
                    _penBackground = AnsiColor.Default;
                    break;
                case >= 90 and <= 97:
                    _penForeground = (AnsiColor)(code - 90 + 9);
                    break;
                case >= 100 and <= 107:
                    _penBackground = (AnsiColor)(code - 100 + 9);
                    break;
                case 38 or 48:
                    index = ConsumeExtendedColor(parts, index, code == 38);
                    break;
                default:
                    break;
            }
        }
    }

    /** 256 色/真彩色: 0-15 映射 16 色板, 16-255 用 xterm 调色板转 RGB(真彩原样), 供真彩渲染路径使用。 */
    private int ConsumeExtendedColor(string[] parts, int index, bool foreground)
    {
        if (index + 1 >= parts.Length)
            return index;
        if (parts[index + 1] == "5" && index + 2 < parts.Length)
        {
            Assign(foreground, int.TryParse(parts[index + 2], out var color) ? FromIndexedColor(color) : CellColor.Default);
            return index + 2;
        }
        if (parts[index + 1] == "2" && index + 4 < parts.Length)
        {
            Assign(foreground,
                int.TryParse(parts[index + 2], out var r) && int.TryParse(parts[index + 3], out var g) && int.TryParse(parts[index + 4], out var b)
                    ? CellColor.FromRgb(Clamp(r), Clamp(g), Clamp(b))
                    : CellColor.Default);
            return index + 4;
        }
        return index;
    }

    private static CellColor FromIndexedColor(int color)
    {
        if (color is < 0)
            return CellColor.Default;
        if (color < 16)
            return CellColor.FromPalette((AnsiColor)(color + 1));
        if (color < 232)
        {
            var cube = color - 16;
            return CellColor.FromRgb(Level(cube / 36), Level(cube / 6 % 6), Level(cube % 6));
        }
        if (color < 256)
        {
            var gray = (byte)(8 + (color - 232) * 10);
            return CellColor.FromRgb(gray, gray, gray);
        }
        return CellColor.Default;
    }

    private static byte Level(int component) => (byte)(component == 0 ? 0 : 55 + (component * 40));

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private void Assign(bool foreground, CellColor color)
    {
        if (foreground)
            _penForeground = color;
        else
            _penBackground = color;
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (CursorY == _scrollBottom)
        {
            ScrollRegionUp();
            return;
        }
        CursorY = Math.Min(Height - 1, CursorY + 1);
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorY == _scrollTop)
        {
            ScrollRegionDown();
            return;
        }
        CursorY = Math.Max(0, CursorY - 1);
    }

    private void ScrollRegionUp()
    {
        if (_scrollTop == 0)
            PushScrollback(_lines.AsSpan(0, Width).ToArray());
        MoveLines(_scrollTop, _scrollTop + 1, _scrollBottom - _scrollTop);
        BlankLine(_scrollBottom);
    }

    private void ScrollRegionDown()
    {
        for (var y = _scrollBottom; y > _scrollTop; y--)
            _lines.AsSpan((y - 1) * Width, Width).CopyTo(_lines.AsSpan(y * Width, Width));
        BlankLine(_scrollTop);
    }

    private void MoveLines(int destinationRow, int sourceRow, int count)
    {
        if (count > 0)
            _lines.AsSpan(sourceRow * Width, count * Width).CopyTo(_lines.AsSpan(destinationRow * Width, count * Width));
    }

    private void PushScrollback(Cell[] line)
    {
        _scrollback.Add(line);
        while (_scrollback.Count > MaxScrollbackLines)
            _scrollback.RemoveAt(0);
    }

    private void BlankLine(int y) => _lines.AsSpan(y * Width, Width).Fill(new Cell(' '));

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var y = CursorY + 1; y < Height; y++)
                    BlankLine(y);
                return;
            case 1:
                EraseLine(1);
                for (var y = 0; y < CursorY; y++)
                    BlankLine(y);
                return;
            case 2:
            case 3:
                for (var y = 0; y < Height; y++)
                    BlankLine(y);
                return;
            default:
                return;
        }
    }

    private void EraseLine(int mode)
    {
        var line = _lines.AsSpan(CursorY * Width, Width);
        switch (mode)
        {
            case 0:
                for (var x = CursorX; x < Width; x++)
                    line[x] = new Cell(' ');
                return;
            case 1:
                for (var x = 0; x <= CursorX && x < Width; x++)
                    line[x] = new Cell(' ');
                return;
            case 2:
                line.Fill(new Cell(' '));
                return;
            default:
                return;
        }
    }

    private void InsertLines(int count)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom)
            return;
        count = Math.Min(count, _scrollBottom - CursorY + 1);
        MoveLines(CursorY + count, CursorY, _scrollBottom - CursorY + 1 - count);
        for (var y = CursorY; y < CursorY + count; y++)
            BlankLine(y);
    }

    private void DeleteLines(int count)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom)
            return;
        count = Math.Min(count, _scrollBottom - CursorY + 1);
        MoveLines(CursorY, CursorY + count, _scrollBottom - CursorY + 1 - count);
        for (var y = _scrollBottom - count + 1; y <= _scrollBottom; y++)
            BlankLine(y);
    }

    private void DeleteCharacters(int count)
    {
        var line = _lines.AsSpan(CursorY * Width, Width);
        count = Math.Min(count, Width - CursorX);
        if (count <= 0)
            return;
        line.Slice(CursorX + count, Width - CursorX - count).CopyTo(line[CursorX..]);
        for (var x = Width - count; x < Width; x++)
            line[x] = new Cell(' ');
    }

    private void InsertCharacters(int count)
    {
        var line = _lines.AsSpan(CursorY * Width, Width);
        count = Math.Min(count, Width - CursorX);
        if (count <= 0)
            return;
        line[CursorX..(Width - count)].CopyTo(line[(CursorX + count)..]);
        for (var x = CursorX; x < CursorX + count; x++)
            line[x] = new Cell(' ');
    }

    private void EraseCharacters(int count)
    {
        var line = _lines.AsSpan(CursorY * Width, Width);
        for (var x = CursorX; x < Math.Min(Width, CursorX + count); x++)
            line[x] = new Cell(' ');
    }

    private void Put(char character)
    {
        if (_wrapPending)
        {
            CursorX = 0;
            LineFeedNoWrapReset();
            _wrapPending = false;
        }
        var advance = TerminalTextWidth.IsWide(character) ? 2 : 1;
        var line = _lines.AsSpan(CursorY * Width, Width);
        if (advance == 2)
        {
            if (CursorX + 1 >= Width)
            {
                line[CursorX] = Cell();
                CursorX = Width - 1;
                _wrapPending = true;
                return;
            }
            line[CursorX] = new Cell(character, _penForeground, _penBackground, _penStyle);
            line[CursorX + 1] = new Cell('\0', _penForeground, _penBackground, _penStyle);
        }
        else
        {
            line[CursorX] = new Cell(character, _penForeground, _penBackground, _penStyle);
        }

        CursorX += advance;
        if (CursorX >= Width)
        {
            CursorX = Width - 1;
            _wrapPending = true;
        }
    }

    private void LineFeedNoWrapReset()
    {
        if (CursorY == _scrollBottom)
            ScrollRegionUp();
        else
            CursorY = Math.Min(Height - 1, CursorY + 1);
    }

    private Cell Cell() => new(' ', _penForeground, _penBackground, _penStyle);

    private static int Count(string body, int fallback)
    {
        var text = body;
        var separator = text.IndexOf(';');
        if (separator >= 0)
            text = text[..separator];
        return int.TryParse(text, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private enum ParseState
    {
        Ground,
        Escape,
        EscapeCharset,
        Csi,
        Osc,
        OscEscape,
    }
}
