using System.Globalization;
using System.Text;

namespace Dsh.Pty;

/**
 * VT 屏: 把 PTY 输出的字节流解释成单元格屏(含滚回缓冲), 供 shell 窗格渲染。
 * 覆盖真实 shell 与全屏 TUI 需要的子集: C0 控制、CSI 光标/擦除/插入删除/滚动区/模式/SGR、OSC 标题与颜色查询、主/备用屏。
 * DCS/APC/PM/SOS 字符串整体丢弃; 设备状态/属性查询经 Reply 事件应答, 没有订阅者(如 daemon)时应答被丢弃。
 */
public sealed class VtScreen
{
    private const int TabWidth = 8;
    private const int DefaultScrollbackLines = 2000;

    /** 渲染行缓冲的栈上限: 一行不超过这么多格就用栈缓冲, 否则退回堆数组。 */
    private const int StackRowCells = 256;

    /** DA1 应答: VT220 级别并声明支持 ANSI 颜色(22); 很多 TUI 用它作为"查询批次结束"的哨兵。 */
    private const string PrimaryDeviceAttributes = "\u001b[?62;22c";

    /** DA2 应答: 终端类型 0、固件版本 10、ROM 1, 与常见 xterm 兼容终端一致。 */
    private const string SecondaryDeviceAttributes = "\u001b[>0;10;1c";

    private const string DeviceStatusOk = "\u001b[0n";

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly List<Cell[]> _scrollback = [];
    private readonly StringBuilder _csi = new();
    private readonly StringBuilder _osc = new();

    private Cell[] _lines;
    private Cell[]? _altLines;
    private int _scrollTop;
    private int _scrollBottom;
    private CursorState _savedCursor;
    private CursorState _mainSavedCursor;
    private CursorState _mainCursor;
    private int _mainScrollTop;
    private int _mainScrollBottom;
    private CellColor _penForeground = CellColor.Default;
    private CellColor _penBackground = CellColor.Default;
    private CellStyle _penStyle = CellStyle.None;
    private bool _wrapPending;
    private bool _autoWrap = true;
    private bool _insertMode;
    private char _lastPrinted = ' ';
    private CharacterSet _g0;
    private CharacterSet _g1;
    private bool _useG1;
    private char _charsetTarget;
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

    /** OSC 0/2 设定的窗口标题。 */
    public string Title { get; private set; } = "";

    public bool IsAlternateScreen => _altLines is not null;

    /** 滚动区上边界(含, 0 起)。 */
    public int ScrollTop => _scrollTop;

    /** 滚动区下边界(含, 0 起)。 */
    public int ScrollBottom => _scrollBottom;

    /** `?2004`: 粘贴内容需要包在 ESC[200~ … ESC[201~ 里送给子进程。 */
    public bool BracketedPaste { get; private set; }

    /** `?1004`: 子进程要求焦点进出时收到 ESC[I / ESC[O。 */
    public bool FocusReporting { get; private set; }

    public TerminalMouseTracking MouseTracking { get; private set; }

    /** `?1006`: 鼠标上报用 SGR 编码(ESC[<b;x;yM), 否则用 X10 字节编码。 */
    public bool SgrMouse { get; private set; }

    public TerminalCursorShape CursorShape { get; private set; }

    /** OSC 10 查询(`?`)应答用的前景色; 非 RGB 时不应答。 */
    public CellColor QueryForeground { get; set; }

    /** OSC 11 查询(`?`)应答用的背景色; 非 RGB 时不应答。 */
    public CellColor QueryBackground { get; set; }

    /** 需要写回子进程的应答(光标位置、设备属性、颜色查询); 在 Feed 调用线程上同步触发。 */
    public event Action<string>? Reply;

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

    /** 按"相对屏幕顶行"的行号取行: 0..Height-1 是当前屏, -1 是最近一行滚回, 越小越旧。 */
    public ReadOnlySpan<Cell> LineAt(int line)
        => line >= 0 ? _lines.AsSpan(line * Width, Width) : ScrollbackRow(-line - 1);

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

    /**
     * 重建可继续消费原始 VT 输出的状态, 而不是仅投影可见字符。
     * 先取消半截控制序列并复位, 再恢复主屏和备用屏, 最后恢复滚动区/输入模式/画笔。
     */
    public string Snapshot()
    {
        var output = new StringBuilder("\u0018\u001bc");
        AppendScreen(output, _altLines ?? _lines);
        if (_altLines is not null)
        {
            AppendCursor(output, _mainSavedCursor, _altLines);
            output.Append("\u001b7");
            AppendRegion(output, _mainScrollTop, _mainScrollBottom);
            AppendCursor(output, _mainCursor, _altLines);
            output.Append("\u001b[?1049h");
            AppendScreen(output, _lines);
        }
        AppendCursor(output, _savedCursor, _lines);
        output.Append("\u001b7");
        AppendRegion(output, _scrollTop, _scrollBottom);
        AppendModes(output);
        output.Append(MouseTracking switch
        {
            TerminalMouseTracking.X10 => "\u001b[?9h",
            TerminalMouseTracking.Normal => "\u001b[?1000h",
            TerminalMouseTracking.ButtonEvent => "\u001b[?1002h",
            TerminalMouseTracking.AnyEvent => "\u001b[?1003h",
            _ => "",
        });
        if (SgrMouse)
            output.Append("\u001b[?1006h");
        AppendCursor(output, CaptureCursor(), _lines);
        output.Append("\u001b]2;").Append(Title).Append("\u001b\\");
        AppendPendingCharset(output);
        return output.ToString();
    }

    /** 独占控制台 attach 的可见屏投影: 留在宿主备用屏, detach 时才能还原调用方的 shell。 */
    internal string ConsoleSnapshot(bool wantsMouse)
    {
        var output = new StringBuilder("\u001b[?1049h\u001b[?7h\u001b[4l\u001b[r");
        AppendScreen(output, _lines);
        AppendRegion(output, _scrollTop, _scrollBottom);
        AppendModes(output);
        // Console attachments keep the owner's mouse negotiation instead of replaying native mouse modes.
        if (wantsMouse)
            output.Append(PtyConsoleBridge.MouseEnableSequence);
        AppendCursor(output, CaptureCursor(), _lines);
        AppendPendingCharset(output);
        return output.ToString();
    }

    private void AppendPendingCharset(StringBuilder output)
    {
        if (_state == ParseState.EscapeCharset)
            output.Append('\u001b').Append(_charsetTarget);
    }

    private void AppendModes(StringBuilder output)
    {
        output.Append(CursorVisible ? "\u001b[?25h" : "\u001b[?25l");
        output.Append(ApplicationCursorKeys ? "\u001b[?1h" : "\u001b[?1l");
        output.Append(_autoWrap ? "\u001b[?7h" : "\u001b[?7l");
        output.Append(_insertMode ? "\u001b[4h" : "\u001b[4l");
        output.Append(BracketedPaste ? "\u001b[?2004h" : "\u001b[?2004l");
        output.Append(FocusReporting ? "\u001b[?1004h" : "\u001b[?1004l");
        output.Append("\u001b[").Append(CursorShape switch
        {
            TerminalCursorShape.Block => 2,
            TerminalCursorShape.Underline => 4,
            TerminalCursorShape.Bar => 6,
            _ => 0,
        }).Append(" q");
    }


    private void AppendScreen(StringBuilder output, Cell[] cells)
    {
        output.Append("\u001b[0m\u001b[2J\u001b[H");
        AppendCharacterSets(output, default);
        for (var y = 0; y < Height; y++)
        {
            output.Append("\u001b[").Append(y + 1).Append(";1H");
            AppendRow(output, cells.AsSpan(y * Width, Width));
            // 满行写完光标仍在最后一格; 此时 EL 会擦掉 tmux 状态栏/边框的最后一格。
        }
    }

    private static void AppendRegion(StringBuilder output, int top, int bottom)
        => output.Append("\u001b[").Append(top + 1).Append(';').Append(bottom + 1).Append('r');

    private void AppendCursor(StringBuilder output, CursorState cursor, Cell[] cells)
    {
        // Cells already contain Unicode glyphs. Replaying them under DEC graphics
        // would translate literal ASCII a second time, including a pending-wrap cell.
        AppendCharacterSets(output, default);
        var x = Math.Clamp(cursor.X, 0, Width - 1);
        var y = Math.Clamp(cursor.Y, 0, Height - 1);
        if (cursor.WrapPending && x == Width - 1)
        {
            // CUP 清除延迟换行; 重写最后一个字形才能把该状态一并恢复。
            var character = cells[y * Width + x].Character;
            if (x > 0 && (character == '\0' || char.IsLowSurrogate(character)
                && char.IsHighSurrogate(cells[y * Width + x - 1].Character)))
                x--;
            var cell = cells[y * Width + x];
            output.Append("\u001b[").Append(y + 1).Append(';').Append(x + 1).Append('H');
            AppendSgr(output, cell);
            output.Append(cell.Character);
            if (char.IsHighSurrogate(cell.Character) && x + 1 < Width
                && char.IsLowSurrogate(cells[y * Width + x + 1].Character))
                output.Append(cells[y * Width + x + 1].Character);
        }
        else
            output.Append("\u001b[").Append(y + 1).Append(';').Append(x + 1).Append('H');
        AppendSgr(output, new Cell(' ', cursor.Foreground, cursor.Background, cursor.Style));
        AppendCharacterSets(output, cursor);
    }

    private static void AppendCharacterSets(StringBuilder output, CursorState cursor)
        => output.Append("\u001b(").Append(cursor.G0 == CharacterSet.DecSpecialGraphics ? '0' : 'B')
            .Append("\u001b)").Append(cursor.G1 == CharacterSet.DecSpecialGraphics ? '0' : 'B')
            .Append(cursor.UseG1 ? '\u000e' : '\u000f');

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
            return;
        // 主屏变矮时把顶部行推进滚回, 保住光标所在行(提示符); 备用屏由全屏程序收到 SIGWINCH 后自行重画。
        var shift = _altLines is null ? Math.Max(0, CursorY - height + 1) : 0;
        for (var y = 0; y < shift; y++)
            PushScrollback(_lines.AsSpan(y * Width, Width).ToArray());
        var resized = CopyLines(_lines, shift, width, height);
        if (_altLines is not null)
            _altLines = CopyLines(_altLines, 0, width, height);
        _lines = resized;
        Width = width;
        Height = height;
        CursorX = Math.Clamp(CursorX, 0, Width - 1);
        CursorY = Math.Clamp(CursorY - shift, 0, Height - 1);
        _wrapPending = false;
        ResetScrollRegion();
    }

    /** 从 source 的第 skipRows 行起按新尺寸截取/补齐, source 的列宽是当前 Width。 */
    private Cell[] CopyLines(Cell[] source, int skipRows, int width, int height)
    {
        var copied = NewLines(width, height);
        var rows = Math.Min(height, Height - skipRows);
        var columns = Math.Min(width, Width);
        for (var y = 0; y < rows; y++)
            source.AsSpan((y + skipRows) * Width, columns).CopyTo(copied.AsSpan(y * width, columns));
        return copied;
    }

    /** 把当前屏(可带滚回偏移)写入目标矩形; 越界部分裁掉。 */
    public void Render(CellGrid grid, ConsoleRect rect, int scrollOffset = 0)
    {
        var rows = Math.Min(rect.Height, Height);
        // stackalloc 必须在循环外: 循环内的栈分配要到方法返回才回收, 满屏渲染会按行数线性吃栈(CA2014)
        Span<Cell> stackRow = stackalloc Cell[StackRowCells];
        for (var y = 0; y < rows; y++)
        {
            var line = LineAt(y - scrollOffset);
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
        if (character is '\u0018' or '\u001a')
        {
            _state = ParseState.Ground;
            _csi.Clear();
            _osc.Clear();
            return;
        }
        // SI/SO remain controls inside a fragmented escape/CSI, not its final byte.
        if (character is '\u000e' or '\u000f'
            && _state is not (ParseState.Osc or ParseState.OscEscape or ParseState.IgnoredString or ParseState.IgnoredStringEscape))
        {
            _useG1 = character == '\u000e';
            return;
        }
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
                else
                    DispatchOsc(_osc.ToString());
                return;
            case ParseState.EscapeCharset:
                if (character is '0' or 'B')
                {
                    var charset = character == '0' ? CharacterSet.DecSpecialGraphics : CharacterSet.Ascii;
                    if (_charsetTarget == '(')
                        _g0 = charset;
                    else if (_charsetTarget == ')')
                        _g1 = charset;
                }
                _state = ParseState.Ground;
                return;
            case ParseState.IgnoredString:
                if (character == '\u001b')
                    _state = ParseState.IgnoredStringEscape;
                return;
            case ParseState.IgnoredStringEscape:
                _state = character == '\\' ? ParseState.Ground : ParseState.IgnoredString;
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
                if (character >= ' ' && !IsZeroWidth(character))
                    Put(MapCharacter(character));
                return;
        }
    }

    private char MapCharacter(char character)
    {
        // VT100 DEC Special Graphics, positions 0x5f..0x7e.
        const string graphics = "\u00a0◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";
        return (_useG1 ? _g1 : _g0) == CharacterSet.DecSpecialGraphics && character is >= '_' and <= '~'
            ? graphics[character - '_']
            : character;
    }

    /** DEL/C1 控制符与组合附加符、零宽格式符不占格: 每格只存一个 char, 附加符无处安放, 占格会让后续列整体右移。 */
    private static bool IsZeroWidth(char character)
        => CharUnicodeInfo.GetUnicodeCategory(character)
            is UnicodeCategory.Control or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;

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
            case '(' or ')' or '*' or '+' or '-' or '.' or '/' or '#' or '%' or ' ':
                _charsetTarget = character;
                _state = ParseState.EscapeCharset;
                return;
            case 'P' or '_' or '^' or 'X':
                _state = ParseState.IgnoredString;
                return;
            case 'c':
                FullReset();
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
                DispatchOsc(_osc.ToString());
                return;
            case '\u001b':
                _state = ParseState.OscEscape;
                return;
            default:
                _osc.Append(character);
                return;
        }
    }

    /** OSC 0/2 设标题; OSC 10/11 的 `?` 查询按 QueryForeground/QueryBackground 应答, 其余编号丢弃。 */
    private void DispatchOsc(string text)
    {
        var separator = text.IndexOf(';');
        if (separator < 0)
            return;
        var code = text[..separator];
        var argument = text[(separator + 1)..];
        if (code is "0" or "2")
            Title = argument;
        else if (argument == "?" && code is "10" or "11")
            ReplyColor(code, code == "10" ? QueryForeground : QueryBackground);
    }

    private void ReplyColor(string code, CellColor color)
    {
        if (color.IsRgb)
            Reply?.Invoke($"\u001b]{code};rgb:{color.R:x2}{color.R:x2}/{color.G:x2}{color.G:x2}/{color.B:x2}{color.B:x2}\u001b\\");
    }

    /** RIS(ESC c): 回到主屏、清屏清滚回、复位画笔与全部模式。 */
    private void FullReset()
    {
        LeaveAltScreen();
        _lines = NewLines(Width, Height);
        _scrollback.Clear();
        ResetScrollRegion();
        (CursorX, CursorY, _wrapPending, _insertMode, _autoWrap) = (0, 0, false, false, true);
        _savedCursor = _mainSavedCursor = _mainCursor = default;
        _g0 = _g1 = CharacterSet.Ascii;
        _useG1 = false;
        _charsetTarget = '\0';
        (_penForeground, _penBackground, _penStyle) = (CellColor.Default, CellColor.Default, CellStyle.None);
        (CursorVisible, ApplicationCursorKeys, BracketedPaste, FocusReporting, SgrMouse) = (true, false, false, false, false);
        (MouseTracking, CursorShape, Title) = (TerminalMouseTracking.None, TerminalCursorShape.Default, "");
    }

    private void DispatchCsi(string parameters, char final)
    {
        var privateMode = parameters.StartsWith('?');
        var body = privateMode ? parameters[1..] : parameters;
        if (IsExtendedCsi(parameters, privateMode, final))
        {
            DispatchExtendedCsi(parameters, final);
            return;
        }
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
            case 'b':
                RepeatLastPrinted(Count(body, 1));
                return;
            case 'c' when Count(body, 0) == 0:
                Reply?.Invoke(PrimaryDeviceAttributes);
                return;
            case 'n':
                ReportStatus(Count(body, 0));
                return;
            default:
                return;
        }
    }

    /**
     * 带 `>`/`<`/`=` 前缀或中间字节(空格、`$`、`!`、`"`)的序列, 以及 `?` 前缀下非模式/擦除的序列, 语义与同名终止符的普通序列不同。
     * 不单独分派的话, vim 的 modifyOtherKeys(CSI > 4;2 m)会被当成 SGR 粗体, kitty 键盘查询(CSI ? u)会被当成恢复光标。
     */
    private static bool IsExtendedCsi(string parameters, bool privateMode, char final)
    {
        if (parameters.Length > 0 && parameters[0] is '>' or '<' or '=')
            return true;
        if (parameters.AsSpan().IndexOfAny(" $!\"") >= 0)
            return true;
        return privateMode && final is not ('h' or 'l' or 'J' or 'K');
    }

    private void DispatchExtendedCsi(string parameters, char final)
    {
        if (final == 'c' && parameters.StartsWith('>'))
            Reply?.Invoke(SecondaryDeviceAttributes);
        else if (final == 'q' && parameters.EndsWith(' '))
            CursorShape = CursorShapeOf(Count(parameters.TrimEnd(), 0));
    }

    /** DECSCUSR: 0 默认, 1/2 方块, 3/4 下划线, 5/6 竖条(奇数闪烁、偶数常亮, 这里不区分闪烁)。 */
    private static TerminalCursorShape CursorShapeOf(int code) => code switch
    {
        1 or 2 => TerminalCursorShape.Block,
        3 or 4 => TerminalCursorShape.Underline,
        5 or 6 => TerminalCursorShape.Bar,
        _ => TerminalCursorShape.Default,
    };

    private void ReportStatus(int code)
    {
        if (code == 5)
            Reply?.Invoke(DeviceStatusOk);
        else if (code == 6)
            Reply?.Invoke($"\u001b[{CursorY + 1};{CursorX + 1}R");
    }

    /** REP: 重复上一个打印字符; 次数封顶一屏, 防止畸形参数让单个序列写出海量格子。 */
    private void RepeatLastPrinted(int count)
    {
        var repeat = Math.Min(count, Width * Height);
        for (var index = 0; index < repeat; index++)
            Put(_lastPrinted);
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
        {
            if (body.Split(';').Contains("4"))
                _insertMode = enabled;
            return;
        }
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
                case "7":
                    _autoWrap = enabled;
                    _wrapPending = false;
                    break;
                case "1049" or "47" or "1047" when enabled:
                    EnterAltScreen();
                    break;
                case "1049" or "47" or "1047":
                    LeaveAltScreen();
                    break;
                case "2004":
                    BracketedPaste = enabled;
                    break;
                case "1004":
                    FocusReporting = enabled;
                    break;
                case "1006":
                    SgrMouse = enabled;
                    break;
                case "9" or "1000" or "1002" or "1003":
                    SetMouseTracking(mode, enabled);
                    break;
                default:
                    break;
            }
        }
    }

    /** 关闭任一鼠标模式即停止上报(与 xterm 一致: 各模式互斥, 后设的覆盖先设的)。 */
    private void SetMouseTracking(string mode, bool enabled)
        => MouseTracking = !enabled ? TerminalMouseTracking.None : mode switch
        {
            "9" => TerminalMouseTracking.X10,
            "1000" => TerminalMouseTracking.Normal,
            "1002" => TerminalMouseTracking.ButtonEvent,
            _ => TerminalMouseTracking.AnyEvent,
        };

    private void EnterAltScreen()
    {
        if (_altLines is not null)
            return;
        _altLines = _lines;
        _lines = NewLines(Width, Height);
        _mainCursor = CaptureCursor();
        _mainSavedCursor = _savedCursor;
        (_mainScrollTop, _mainScrollBottom) = (_scrollTop, _scrollBottom);
        _savedCursor = default;
        CursorX = 0;
        CursorY = 0;
        _wrapPending = false;
        ResetScrollRegion();
    }

    private void LeaveAltScreen()
    {
        if (_altLines is null)
            return;
        _lines = _altLines;
        _altLines = null;
        ApplyCursor(_mainCursor);
        _savedCursor = _mainSavedCursor;
        _scrollTop = Math.Clamp(_mainScrollTop, 0, Height - 1);
        _scrollBottom = Math.Clamp(_mainScrollBottom, _scrollTop, Height - 1);
    }

    private enum CharacterSet { Ascii, DecSpecialGraphics }

    private readonly record struct CursorState(int X, int Y, CellColor Foreground, CellColor Background,
        CellStyle Style, bool WrapPending, CharacterSet G0, CharacterSet G1, bool UseG1);

    private CursorState CaptureCursor()
        => new(CursorX, CursorY, _penForeground, _penBackground, _penStyle, _wrapPending, _g0, _g1, _useG1);

    private void ApplyCursor(CursorState cursor)
    {
        CursorX = Math.Clamp(cursor.X, 0, Width - 1);
        CursorY = Math.Clamp(cursor.Y, 0, Height - 1);
        (_penForeground, _penBackground, _penStyle) = (cursor.Foreground, cursor.Background, cursor.Style);
        _wrapPending = cursor.WrapPending && CursorX == Width - 1;
        (_g0, _g1, _useG1) = (cursor.G0, cursor.G1, cursor.UseG1);
    }

    private void SaveCursor() => _savedCursor = CaptureCursor();

    private void RestoreCursor() => ApplyCursor(_savedCursor);

    private static void AppendRow(StringBuilder output, ReadOnlySpan<Cell> row)
    {
        var last = row.Length - 1;
        while (last >= 0 && row[last] == default)
            last--;

        var current = default(Cell);
        for (var x = 0; x <= last; x++)
        {
            var cell = row[x];
            if (cell.Character == '\0')
                continue;   // 宽字符的续格: 由前一个宽字符占位

            if (cell.Foreground != current.Foreground || cell.Background != current.Background || cell.Style != current.Style)
            {
                AppendSgr(output, cell);
                current = cell;
            }

            output.Append(cell.Character);
        }

        output.Append("\u001b[0m");
    }

    private static void AppendSgr(StringBuilder output, Cell cell)
    {
        output.Append("\u001b[0");
        if ((cell.Style & CellStyle.Bold) != 0)
            output.Append(";1");
        if ((cell.Style & CellStyle.Dim) != 0)
            output.Append(";2");
        if ((cell.Style & CellStyle.Italic) != 0)
            output.Append(";3");
        if ((cell.Style & CellStyle.Underline) != 0)
            output.Append(";4");
        if ((cell.Style & CellStyle.Reverse) != 0)
            output.Append(";7");
        if ((cell.Style & CellStyle.Strikethrough) != 0)
            output.Append(";9");
        if (!cell.Foreground.IsDefault)
            output.Append(';').Append(ForegroundSegment(cell.Foreground));
        if (!cell.Background.IsDefault)
            output.Append(';').Append(BackgroundSegment(cell.Background));
        output.Append('m');
    }

    private static string ForegroundSegment(CellColor color)
        => color.IsRgb ? $"38;2;{color.R};{color.G};{color.B}" : ForegroundCode(color.IsPalette ? color.PaletteColor : AnsiColor.Default).ToString();

    private static string BackgroundSegment(CellColor color)
        => color.IsRgb ? $"48;2;{color.R};{color.G};{color.B}" : (ForegroundCode(color.IsPalette ? color.PaletteColor : AnsiColor.Default) + 10).ToString();

    /** 调色板 1..8 → 30..37, 9..16 → 90..97(背景再 +10)。 */
    private static int ForegroundCode(AnsiColor color)
        => color <= AnsiColor.White ? 29 + (int)color : 81 + (int)color;

    private void SelectGraphicRendition(string body)
    {
        if (body.Length == 0)
            body = "0";
        var parts = body.Split(';');
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Contains(':'))
            {
                SelectColonRendition(parts[index].Split(':'));
                continue;
            }
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
                case 3:
                    _penStyle |= CellStyle.Italic;
                    break;
                case 4 or 21:
                    _penStyle |= CellStyle.Underline;
                    break;
                case 7:
                    _penStyle |= CellStyle.Reverse;
                    break;
                case 9:
                    _penStyle |= CellStyle.Strikethrough;
                    break;
                case 22:
                    _penStyle &= ~(CellStyle.Bold | CellStyle.Dim);
                    break;
                case 23:
                    _penStyle &= ~CellStyle.Italic;
                    break;
                case 24:
                    _penStyle &= ~CellStyle.Underline;
                    break;
                case 27:
                    _penStyle &= ~CellStyle.Reverse;
                    break;
                case 29:
                    _penStyle &= ~CellStyle.Strikethrough;
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

    /**
     * 冒号子参数形式(ITU T.416): `4:n` 下划线样式(0 关), `38:2:[色彩空间]:r:g:b` / `38:5:n` 前景, 48 背景。
     * 58(下划线颜色)等其余编号忽略。
     */
    private void SelectColonRendition(string[] sub)
    {
        if (sub[0] == "4")
        {
            _penStyle = sub.Length > 1 && sub[1] == "0" ? _penStyle & ~CellStyle.Underline : _penStyle | CellStyle.Underline;
            return;
        }
        if (sub[0] is not ("38" or "48") || sub.Length < 3)
            return;
        var foreground = sub[0] == "38";
        if (sub[1] == "5")
            Assign(foreground, int.TryParse(sub[2], out var indexed) ? FromIndexedColor(indexed) : CellColor.Default);
        else if (sub[1] == "2" && sub.Length >= 5)
            Assign(foreground, RgbFrom(sub.AsSpan(sub.Length - 3)));
    }

    private static CellColor RgbFrom(ReadOnlySpan<string> components)
        => int.TryParse(components[0], out var r) && int.TryParse(components[1], out var g) && int.TryParse(components[2], out var b)
            ? CellColor.FromRgb(Clamp(r), Clamp(g), Clamp(b))
            : CellColor.Default;

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
        // 备用屏是全屏程序的画布, 滚出去的行不属于用户的历史输出, 不进滚回(与 xterm 一致)。
        if (_scrollTop == 0 && _altLines is null)
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

    private void BlankLine(int y) => _lines.AsSpan(y * Width, Width).Fill(Blank());

    /** 擦除/滚入的空白格带当前背景色(BCE): TERM=xterm-256color 声明了 bce, ncurses 程序依赖它铺底色。 */
    private Cell Blank() => new(' ', CellColor.Default, _penBackground);

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
                for (var y = 0; y < Height; y++)
                    BlankLine(y);
                return;
            case 3:
                _scrollback.Clear();
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
                    line[x] = Blank();
                return;
            case 1:
                for (var x = 0; x <= CursorX && x < Width; x++)
                    line[x] = Blank();
                return;
            case 2:
                line.Fill(Blank());
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
            line[x] = Blank();
    }

    private void InsertCharacters(int count)
    {
        var line = _lines.AsSpan(CursorY * Width, Width);
        count = Math.Min(count, Width - CursorX);
        if (count <= 0)
            return;
        line[CursorX..(Width - count)].CopyTo(line[(CursorX + count)..]);
        for (var x = CursorX; x < CursorX + count; x++)
            line[x] = Blank();
    }

    private void EraseCharacters(int count)
    {
        var line = _lines.AsSpan(CursorY * Width, Width);
        for (var x = CursorX; x < Math.Min(Width, CursorX + count); x++)
            line[x] = Blank();
    }

    private void Put(char character)
    {
        var advance = TerminalTextWidth.IsWide(character) && Width > 1 ? 2 : 1;
        if (advance == 2 && CursorX + 1 >= Width && !_wrapPending && _autoWrap)
        {
            // 宽字符放不下行尾最后一格: 留空该格并折到下一行再写, 而不是丢掉字符。
            _lines[(CursorY * Width) + CursorX] = Cell();
            _wrapPending = true;
        }
        if (_wrapPending)
        {
            CursorX = 0;
            LineFeedNoWrapReset();
            _wrapPending = false;
        }
        if (_insertMode)
            InsertCharacters(advance);
        var line = _lines.AsSpan(CursorY * Width, Width);
        ClearWideRemnants(line, CursorX, advance);
        line[CursorX] = new Cell(character, _penForeground, _penBackground, _penStyle);
        if (advance == 2 && CursorX + 1 < Width)
            line[CursorX + 1] = new Cell('\0', _penForeground, _penBackground, _penStyle);
        _lastPrinted = character;
        CursorX += advance;
        if (CursorX >= Width)
        {
            CursorX = Width - 1;
            _wrapPending = _autoWrap;
        }
    }

    /** 覆盖宽字符的任一半时把另一半清成空格, 否则会留下没有本体的续格或半个宽字形。 */
    private static void ClearWideRemnants(Span<Cell> line, int x, int advance)
    {
        if (line[x].Character == '\0' && x > 0)
            line[x - 1] = line[x - 1] with { Character = ' ' };
        var end = x + advance;
        if (end < line.Length && line[end].Character == '\0')
            line[end] = line[end] with { Character = ' ' };
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
        IgnoredString,
        IgnoredStringEscape,
    }
}
